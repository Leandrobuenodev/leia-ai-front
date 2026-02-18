using Azure;
using Azure.Data.Tables;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using System.Text;
using System.Text.Json; // Usando System.Text.Json nativo
using System.Net.Http.Headers;

namespace KumulusAI;

public class AskAI
{
    private readonly IConfiguration _config;
    // Cliente HTTP estático para performance
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
            logger.LogInformation(">>> INICIANDO ASKAI (MODO JSON DOCUMENT) <<<");

            // 1. Configurações
            string endpoint = _config["AZURE_OPENAI_ENDPOINT"] ?? "";
            string key = _config["AZURE_OPENAI_KEY"] ?? "";
            string deployment = _config["AZURE_OPENAI_DEPLOYMENT_NAME"] ?? "gpt-4o-mini";
            string connString = _config["AzureWebJobsStorage"] ?? "";

            // 2. Leitura Segura do Body
            string requestBodyStr = await new StreamReader(req.Body).ReadToEndAsync();

            // Variáveis Padrão
            string userPrompt = "Analise esta imagem.";
            string sessionId = Guid.NewGuid().ToString();
            string imageBase64 = "";

            // Parse Seguro (Isso remove os avisos CS8602)
            if (!string.IsNullOrWhiteSpace(requestBodyStr))
            {
                try
                {
                    using (JsonDocument doc = JsonDocument.Parse(requestBodyStr))
                    {
                        var root = doc.RootElement;

                        // Tenta ler com segurança. Se não achar, mantém o padrão.
                        if (root.TryGetProperty("prompt", out var p)) userPrompt = p.GetString() ?? "Analise.";
                        if (root.TryGetProperty("sessionId", out var s)) sessionId = s.GetString() ?? Guid.NewGuid().ToString();
                        if (root.TryGetProperty("imageBase64", out var i)) imageBase64 = i.GetString() ?? "";
                    }
                }
                catch (Exception jsonEx)
                {
                    logger.LogWarning($"JSON inválido recebido, usando padrões. Erro: {jsonEx.Message}");
                }
            }

            logger.LogInformation($"Dados: Sessão={sessionId} | Tem Imagem={!string.IsNullOrEmpty(imageBase64)}");

            // 3. Montar Mensagens
            var messages = new List<object>();
            messages.Add(new { role = "system", content = "Você é a LeIA. Responda em Markdown." });

            // Histórico (Blindado)
            if (!string.IsNullOrEmpty(connString))
            {
                try
                {
                    var tableClient = new TableClient(connString, "HistoricoConversas");
                    // Não aguarda criação para ser rápido
                    var history = tableClient.QueryAsync<ChatLogEntity>(filter: $"PartitionKey eq '{sessionId}'");
                    await foreach (var entity in history)
                    {
                        messages.Add(new { role = "user", content = entity.UserMessage });
                        messages.Add(new { role = "assistant", content = entity.AIMessage });
                    }
                }
                catch { /* Ignora falha de histórico */ }
            }

            // 4. Lógica da Imagem
            if (!string.IsNullOrEmpty(imageBase64))
            {
                // Limpeza da string base64
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

            var payload = new { messages = messages, max_tokens = 800 };
            var jsonContent = JsonSerializer.Serialize(payload);

            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, apiUrl);
            httpRequest.Headers.Add("api-key", key);
            httpRequest.Content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

            var response = await _httpClient.SendAsync(httpRequest);
            string responseText = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                throw new Exception($"OpenAI Erro ({response.StatusCode}): {responseText}");
            }

            // 6. Resposta
            using var responseDoc = JsonDocument.Parse(responseText);
            string aiAnswer = responseDoc.RootElement
                                         .GetProperty("choices")[0]
                                         .GetProperty("message")
                                         .GetProperty("content")
                                         .GetString() ?? "Sem resposta.";

            // Salvar (Blindado)
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
            logger.LogError(ex, "ERRO CRÍTICO");
            var errorRes = req.CreateResponse(System.Net.HttpStatusCode.InternalServerError);
            await errorRes.WriteStringAsync($"ERRO: {ex.Message}");
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