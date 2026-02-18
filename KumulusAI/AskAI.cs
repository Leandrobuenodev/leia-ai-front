using Azure;
using Azure.Data.Tables;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace KumulusAI;

public class AskAI
{
    private readonly IConfiguration _config;
    private readonly HttpClient _httpClient;

    public AskAI(IConfiguration config, IHttpClientFactory httpClientFactory)
    {
        _config = config;
        _httpClient = httpClientFactory.CreateClient();
    }

    [Function("AskAI")]
    public async Task<HttpResponseData> Run([HttpTrigger(AuthorizationLevel.Anonymous, "post")] HttpRequestData req)
    {
        var logger = req.FunctionContext.GetLogger("AskAI");

        try
        {
            // 1. Configurações
            string endpoint = _config["AZURE_OPENAI_ENDPOINT"] ?? "";
            string key = _config["AZURE_OPENAI_KEY"] ?? "";
            string deploymentName = _config["AZURE_OPENAI_DEPLOYMENT_NAME"] ?? "gpt-4o-mini";
            string connectionString = _config["AzureWebJobsStorage"] ?? "";

            // URL da API REST (Manual)
            // Formato: https://{endpoint}/openai/deployments/{deployment}/chat/completions?api-version=2024-02-15-preview
            if (endpoint.EndsWith("/")) endpoint = endpoint.TrimEnd('/');
            string apiUrl = $"{endpoint}/openai/deployments/{deploymentName}/chat/completions?api-version=2024-02-15-preview";

            // 2. Leitura do Request
            string requestBodyStr = await new StreamReader(req.Body).ReadToEndAsync();
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var requestBody = JsonSerializer.Deserialize<PromptRequest>(requestBodyStr, options);

            string sessionId = requestBody?.SessionId ?? Guid.NewGuid().ToString();
            string userPrompt = string.IsNullOrWhiteSpace(requestBody?.Prompt) ? "Analise esta imagem" : requestBody.Prompt;
            string imageBase64 = requestBody?.ImageBase64 ?? "";

            // 3. Montar Lista de Mensagens (Protocolo OpenAI JSON)
            var messagesList = new List<object>();

            // System Message
            messagesList.Add(new { role = "system", content = "Você é a LeIA, assistente virtual. Responda usando formatação Markdown." });

            // Histórico (Recuperar do Banco)
            var tableClient = new TableClient(connectionString, "HistoricoConversas");
            await tableClient.CreateIfNotExistsAsync();

            try
            {
                var history = tableClient.QueryAsync<ChatLogEntity>(filter: $"PartitionKey eq '{sessionId}'");
                await foreach (var entity in history)
                {
                    messagesList.Add(new { role = "user", content = entity.UserMessage });
                    messagesList.Add(new { role = "assistant", content = entity.AIMessage });
                }
            }
            catch { /* Ignora erro de histórico para não travar */ }

            // 4. Adicionar Mensagem Atual (Lógica da Imagem)
            if (!string.IsNullOrEmpty(imageBase64))
            {
                if (imageBase64.Contains(",")) imageBase64 = imageBase64.Split(',')[1];
                imageBase64 = imageBase64.Trim().Replace(" ", "+").Replace("\n", "").Replace("\r", "");

                string dataUrl = $"data:image/jpeg;base64,{imageBase64}";

                // Payload Multimodal Manual
                messagesList.Add(new
                {
                    role = "user",
                    content = new object[] {
                        new { type = "text", text = userPrompt },
                        new { type = "image_url", image_url = new { url = dataUrl } }
                    }
                });
            }
            else
            {
                messagesList.Add(new { role = "user", content = userPrompt });
            }

            // 5. Enviar para OpenAI (HTTP Puro)
            var payload = new
            {
                messages = messagesList,
                max_tokens = 1000,
                temperature = 0.7
            };

            var jsonPayload = JsonSerializer.Serialize(payload);
            var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

            _httpClient.DefaultRequestHeaders.Clear();
            _httpClient.DefaultRequestHeaders.Add("api-key", key);

            var response = await _httpClient.PostAsync(apiUrl, content);

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync();
                throw new Exception($"OpenAI Error ({response.StatusCode}): {errorBody}");
            }

            // 6. Ler Resposta
            var responseJson = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(responseJson);
            string aiResponse = doc.RootElement
                                   .GetProperty("choices")[0]
                                   .GetProperty("message")
                                   .GetProperty("content")
                                   .GetString() ?? "";

            // 7. Salvar e Retornar
            await tableClient.AddEntityAsync(new ChatLogEntity
            {
                PartitionKey = sessionId,
                RowKey = DateTime.UtcNow.Ticks.ToString(),
                UserMessage = userPrompt,
                AIMessage = aiResponse,
                ChatTitle = "Conversa LeIA"
            });

            var res = req.CreateResponse(System.Net.HttpStatusCode.OK);
            await res.WriteAsJsonAsync(new { answer = aiResponse, sessionId = sessionId });
            return res;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "ERRO NO HTTP MANUAL");
            var errorRes = req.CreateResponse(System.Net.HttpStatusCode.InternalServerError);
            await errorRes.WriteStringAsync($"ERRO: {ex.Message}");
            return errorRes;
        }
    }
}

// Entidades
public class ChatLogEntity : ITableEntity
{
    public string PartitionKey { get; set; } = "";
    public string RowKey { get; set; } = "";
    public DateTimeOffset? Timestamp { get; set; }
    public ETag ETag { get; set; }
    public string UserMessage { get; set; } = "";
    public string AIMessage { get; set; } = "";
    public string ChatTitle { get; set; } = "";
}

public class PromptRequest
{
    public string? Prompt { get; set; }
    public string? SessionId { get; set; }
    public string? ImageBase64 { get; set; }
}