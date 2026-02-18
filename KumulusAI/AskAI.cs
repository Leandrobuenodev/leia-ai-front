using Azure;
using Azure.Data.Tables;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using System.Text;
using System.Text.Json; // Usa o nativo do .NET 8
using System.Net.Http.Headers;

namespace KumulusAI;

public class AskAI
{
    private readonly IConfiguration _config;
    // Cliente HTTP estático e reutilizável
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
            logger.LogInformation(">>> INICIANDO ASKAI OTIMIZADO <<<");

            // 1. Configuração Segura
            string endpoint = _config["AZURE_OPENAI_ENDPOINT"] ?? "";
            string key = _config["AZURE_OPENAI_KEY"] ?? "";
            string deployment = _config["AZURE_OPENAI_DEPLOYMENT_NAME"] ?? "gpt-4o-mini";
            string connString = _config["AzureWebJobsStorage"] ?? "";

            if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(key))
            {
                // Se isso acontecer, o erro aparecerá no navegador
                throw new Exception("CONFIG ERROR: Endpoint ou Key da OpenAI faltando.");
            }

            // Normaliza URL
            if (endpoint.EndsWith("/")) endpoint = endpoint.TrimEnd('/');
            string apiUrl = $"{endpoint}/openai/deployments/{deployment}/chat/completions?api-version=2024-02-15-preview";

            // 2. Leitura Otimizada (STREAM) - Evita estouro de memória com imagens grandes
            PromptRequest? requestBody = null;
            try
            {
                var jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                // Lê direto do stream sem criar string gigante
                requestBody = await JsonSerializer.DeserializeAsync<PromptRequest>(req.Body, jsonOptions);
            }
            catch (Exception exJson)
            {
                throw new Exception($"JSON ERROR: Falha ao ler requisição. {exJson.Message}");
            }

            string sessionId = requestBody?.SessionId ?? Guid.NewGuid().ToString();
            string userPrompt = string.IsNullOrWhiteSpace(requestBody?.Prompt) ? "Analise esta imagem." : requestBody.Prompt;
            string imageBase64 = requestBody?.ImageBase64 ?? "";

            // 3. Montagem do Payload
            var messages = new List<object>();
            messages.Add(new { role = "system", content = "Você é a LeIA. Responda em Markdown." });

            // Recuperar Histórico (Blindado: falha no banco não trava a IA)
            if (!string.IsNullOrEmpty(connString))
            {
                try
                {
                    var tableClient = new TableClient(connString, "HistoricoConversas");
                    // Não damos CreateIfNotExists aqui para ganhar tempo, assumimos que existe ou falha silenciosamente
                    var history = tableClient.QueryAsync<ChatLogEntity>(filter: $"PartitionKey eq '{sessionId}'");
                    await foreach (var entity in history)
                    {
                        messages.Add(new { role = "user", content = entity.UserMessage });
                        messages.Add(new { role = "assistant", content = entity.AIMessage });
                    }
                }
                catch (Exception exHist) { logger.LogWarning($"Histórico ignorado: {exHist.Message}"); }
            }

            // Adicionar Imagem ou Texto
            if (!string.IsNullOrEmpty(imageBase64))
            {
                // Limpeza agressiva
                if (imageBase64.Contains(",")) imageBase64 = imageBase64.Split(',')[1];
                imageBase64 = imageBase64.Trim().Replace("\r", "").Replace("\n", "");

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

            // 4. Envio HTTP Seguro (HttpRequestMessage)
            var payload = new { messages = messages, max_tokens = 1000 };
            var jsonPayload = JsonSerializer.Serialize(payload);

            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, apiUrl);
            httpRequest.Headers.Add("api-key", key); // Header seguro
            httpRequest.Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

            logger.LogInformation("Enviando request para OpenAI...");
            var response = await _httpClient.SendAsync(httpRequest);
            string responseText = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                throw new Exception($"OPENAI ERROR ({response.StatusCode}): {responseText}");
            }

            // 5. Processar Resposta
            using var doc = JsonDocument.Parse(responseText);
            string aiAnswer = doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "";

            // Salvar no Banco (Blindado)
            if (!string.IsNullOrEmpty(connString))
            {
                try
                {
                    var tableClient = new TableClient(connString, "HistoricoConversas");
                    // Fire-and-forget (não esperamos muito para não travar o usuário)
                    await tableClient.CreateIfNotExistsAsync();
                    await tableClient.AddEntityAsync(new ChatLogEntity
                    {
                        PartitionKey = sessionId,
                        RowKey = DateTime.UtcNow.Ticks.ToString(),
                        UserMessage = userPrompt,
                        AIMessage = aiAnswer,
                        ChatTitle = "Conversa"
                    });
                }
                catch { /* Ignora */ }
            }

            // 6. Retorno
            var res = req.CreateResponse(System.Net.HttpStatusCode.OK);
            await res.WriteAsJsonAsync(new { answer = aiAnswer, sessionId = sessionId });
            return res;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "CRASH NO RUN");
            var errorRes = req.CreateResponse(System.Net.HttpStatusCode.InternalServerError);
            // Retorna o erro exato para você ver no navegador
            await errorRes.WriteStringAsync($"ERRO FATAL: {ex.Message}");
            return errorRes;
        }
    }
}

// Entidades Mínimas
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