using Azure;
using Azure.Data.Tables;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using System.Text;
using System.Text.Json;
using System.Net.Http.Headers;

namespace KumulusAI;

public class AskAI
{
    private readonly IConfiguration _config;
    private static readonly HttpClient _httpClient = new HttpClient();

    public AskAI(IConfiguration config)
    {
        _config = config;
    }

    [Function("AskAI")]
    public async Task<HttpResponseData> Run([HttpTrigger(AuthorizationLevel.Anonymous, "post")] HttpRequestData req)
    {
        var logger = req.FunctionContext.GetLogger("AskAI");

        try
        {
            // ... dentro da Function AskAI
            string endpoint = _config["AZURE_OPENAI_ENDPOINT"] ?? "";
            string key = _config["AZURE_OPENAI_KEY"] ?? "";
            string deployment = _config["AZURE_OPENAI_DEPLOYMENT_NAME"] ?? "gpt-4o-mini";
            // Nova linha com o nome permitido:
            string connString = _config["STORAGE_CONNECTION_STRING"] ?? "";

            // 2. Leitura do Body
            string requestBodyStr = await new StreamReader(req.Body).ReadToEndAsync();

            // Valores Padrão
            string userPrompt = "Analise esta imagem.";
            string sessionId = Guid.NewGuid().ToString();
            string imageBase64 = "";

            // 3. Parse Seguro (AQUI ESTAVA O ERRO DO NULL)
            if (!string.IsNullOrWhiteSpace(requestBodyStr))
            {
                try
                {
                    using (JsonDocument doc = JsonDocument.Parse(requestBodyStr))
                    {
                        var root = doc.RootElement;

                        // Verifica se existe E se não é nulo antes de ler
                        if (root.TryGetProperty("prompt", out var p) && p.ValueKind == JsonValueKind.String)
                            userPrompt = p.GetString() ?? "Analise.";

                        if (root.TryGetProperty("sessionId", out var s) && s.ValueKind == JsonValueKind.String)
                            sessionId = s.GetString() ?? Guid.NewGuid().ToString();

                        if (root.TryGetProperty("imageBase64", out var i) && i.ValueKind == JsonValueKind.String)
                            imageBase64 = i.GetString() ?? "";
                    }
                }
                catch (Exception jsonEx)
                {
                    logger.LogWarning($"JSON inválido: {jsonEx.Message}");
                }
            }

            // 4. Montar Payload OpenAI
            var messages = new List<object>();
            messages.Add(new { role = "system", content = "Você é a LeIA. Responda em Markdown." });

            // Histórico
            if (!string.IsNullOrEmpty(connString))
            {
                try
                {
                    var tableClient = new TableClient(connString, "HistoricoConversas");
                    var history = tableClient.QueryAsync<ChatLogEntity>(filter: $"PartitionKey eq '{sessionId}'");
                    await foreach (var entity in history)
                    {
                        messages.Add(new { role = "user", content = entity.UserMessage });
                        messages.Add(new { role = "assistant", content = entity.AIMessage });
                    }
                }
                catch { }
            }

            // Lógica da Imagem
            if (!string.IsNullOrEmpty(imageBase64))
            {
                // Tratamento de prefixo DataURL caso o frontend mande completo
                if (imageBase64.Contains(","))
                {
                    var parts = imageBase64.Split(',');
                    if (parts.Length > 1) imageBase64 = parts[1];
                }

                imageBase64 = imageBase64.Trim().Replace("\r", "").Replace("\n", "").Replace(" ", "+");

                messages.Add(new
                {
                    role = "user",
                    content = new object[] {
                        new { type = "text", text = userPrompt },
                        new { type = "image_url", image_url = new { url = $"data:image/jpeg;base64,{imageBase64}" } }
                    }
                });
            }
            else
            {
                messages.Add(new { role = "user", content = userPrompt });
            }

            // 5. Envio HTTP
            string apiUrl = $"{endpoint.TrimEnd('/')}/openai/deployments/{deployment}/chat/completions?api-version=2024-02-15-preview";

            var payload = new { messages = messages, max_tokens = 1000 };
            var jsonPayload = JsonSerializer.Serialize(payload);

            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, apiUrl);
            httpRequest.Headers.Add("api-key", key);
            httpRequest.Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

            var response = await _httpClient.SendAsync(httpRequest);

            if (!response.IsSuccessStatusCode)
            {
                var errorTxt = await response.Content.ReadAsStringAsync();
                throw new Exception($"OpenAI Erro ({response.StatusCode}): {errorTxt}");
            }

            // 6. Resposta
            string responseText = await response.Content.ReadAsStringAsync();
            using var responseDoc = JsonDocument.Parse(responseText);
            string aiAnswer = responseDoc.RootElement
                                         .GetProperty("choices")[0]
                                         .GetProperty("message")
                                         .GetProperty("content")
                                         .GetString() ?? "";

            // Salvar
            if (!string.IsNullOrEmpty(connString))
            {
                try
                {
                    var tableClient = new TableClient(connString, "HistoricoConversas");
                    await tableClient.CreateIfNotExistsAsync();
                    await tableClient.AddEntityAsync(new ChatLogEntity
                    {
                        PartitionKey = sessionId,
                        RowKey = DateTime.UtcNow.Ticks.ToString(),
                        UserMessage = userPrompt,
                        AIMessage = aiAnswer
                    });
                }
                catch { }
            }

            var res = req.CreateResponse(System.Net.HttpStatusCode.OK);
            await res.WriteAsJsonAsync(new { answer = aiAnswer, sessionId = sessionId });
            return res;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "ERRO NO RUN");
            var errorRes = req.CreateResponse(System.Net.HttpStatusCode.InternalServerError);
            // Retorna JSON de erro para o front não dar erro de parse
            await errorRes.WriteAsJsonAsync(new { answer = $"☠️ Erro no servidor: {ex.Message}" });
            return errorRes;
        }
    }
}

public class ChatLogEntity : ITableEntity
{
    public string PartitionKey { get; set; } = "";
    public string RowKey { get; set; } = "";
    public DateTimeOffset? Timestamp { get; set; }
    public ETag ETag { get; set; }
    public string UserMessage { get; set; } = "";
    public string AIMessage { get; set; } = "";
}