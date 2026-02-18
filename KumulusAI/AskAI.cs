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
            logger.LogInformation(">>> INICIANDO PROCESSAMENTO vFINAL <<<");

            // 1. Configurações
            string endpoint = _config["AZURE_OPENAI_ENDPOINT"] ?? "";
            string key = _config["AZURE_OPENAI_KEY"] ?? "";
            string deployment = _config["AZURE_OPENAI_DEPLOYMENT_NAME"] ?? "gpt-4o-mini";
            string connString = _config["AzureWebJobsStorage"] ?? "";

            // 2. Leitura Segura do Body
            string requestBodyStr;
            using (var reader = new StreamReader(req.Body))
            {
                requestBodyStr = await reader.ReadToEndAsync();
            }

            if (string.IsNullOrEmpty(requestBodyStr)) throw new Exception("Body vazio recebido.");

            PromptRequest? requestBody;
            try
            {
                var jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                requestBody = JsonSerializer.Deserialize<PromptRequest>(requestBodyStr, jsonOptions);
            }
            catch
            {
                throw new Exception("Falha ao deserializar JSON.");
            }

            string sessionId = requestBody?.SessionId ?? Guid.NewGuid().ToString();
            string userPrompt = string.IsNullOrWhiteSpace(requestBody?.Prompt) ? "Analise..." : requestBody.Prompt;
            string imageBase64 = requestBody?.ImageBase64 ?? "";

            // 3. Montar Mensagens
            var messages = new List<object>();
            messages.Add(new { role = "system", content = "Você é a LeIA. Responda em Markdown." });

            // Recuperar Histórico
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
                catch { /* Ignora */ }
            }

            // 4. Lógica da Imagem
            if (!string.IsNullOrEmpty(imageBase64))
            {
                logger.LogInformation($"Imagem detectada. Tamanho string: {imageBase64.Length}");
                if (imageBase64.Contains(",")) imageBase64 = imageBase64.Split(',')[1];

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

            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, apiUrl);
            httpRequest.Headers.Add("api-key", key);
            httpRequest.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

            var response = await _httpClient.SendAsync(httpRequest);
            string responseText = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                throw new Exception($"OPENAI ERROR: {responseText}");
            }

            // 6. Resposta
            using var doc = JsonDocument.Parse(responseText);
            string aiAnswer = doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "";

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
            await errorRes.WriteStringAsync($"ERRO: {ex.Message}");
            return errorRes;
        }
    }
}

public class ChatLogEntity : ITableEntity { public string PartitionKey { get; set; } = ""; public string RowKey { get; set; } = ""; public DateTimeOffset? Timestamp { get; set; } public ETag ETag { get; set; } public string UserMessage { get; set; } = ""; public string AIMessage { get; set; } = ""; }
public class PromptRequest { public string? Prompt { get; set; } public string? SessionId { get; set; } public string? ImageBase64 { get; set; } }