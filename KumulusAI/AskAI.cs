using Azure;
using Azure.AI.OpenAI;
using Azure.Data.Tables;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using System.IO;

namespace KumulusAI;

public class AskAI
{
    private readonly IConfiguration _config;

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
            logger.LogInformation(">>> INICIANDO EXECUÇÃO (VERSÃO BETA.15) <<<");

            string endpoint = _config["AZURE_OPENAI_ENDPOINT"] ?? "";
            string key = _config["AZURE_OPENAI_KEY"] ?? "";
            string connectionString = _config["AzureWebJobsStorage"] ?? "";
            string deploymentName = _config["AZURE_OPENAI_DEPLOYMENT_NAME"] ?? "gpt-4o-mini";

            if (string.IsNullOrEmpty(endpoint)) throw new Exception("Endpoint da OpenAI está vazio ou nulo.");
            if (string.IsNullOrEmpty(key)) throw new Exception("Chave da OpenAI está vazia ou nula.");

            // Inicialização do cliente
            var aiClient = new OpenAIClient(new Uri(endpoint), new AzureKeyCredential(key));
            var tableClient = new TableClient(connectionString, "HistoricoConversas");
            await tableClient.CreateIfNotExistsAsync();

            string requestBodyStr = await new StreamReader(req.Body).ReadToEndAsync();
            PromptRequest? requestBody;
            try
            {
                requestBody = System.Text.Json.JsonSerializer.Deserialize<PromptRequest>(requestBodyStr, new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            catch
            {
                requestBody = new PromptRequest(); // Fallback
            }

            string sessionId = requestBody?.SessionId ?? Guid.NewGuid().ToString();
            string userPrompt = string.IsNullOrWhiteSpace(requestBody?.Prompt) ? "Analise esta imagem" : requestBody.Prompt;
            string imageBase64 = requestBody?.ImageBase64 ?? "";

            var options = new ChatCompletionsOptions { DeploymentName = deploymentName, MaxTokens = 1000 };
            options.Messages.Add(new ChatRequestSystemMessage("Você é a LeIA. Responda em Markdown."));

            // Histórico
            try
            {
                var history = tableClient.QueryAsync<ChatLogEntity>(filter: $"PartitionKey eq '{sessionId}'");
                await foreach (var entity in history)
                {
                    options.Messages.Add(new ChatRequestUserMessage(entity.UserMessage));
                    options.Messages.Add(new ChatRequestAssistantMessage(entity.AIMessage));
                }
            }
            catch (Exception exHist)
            {
                logger.LogWarning($"Histórico indisponível: {exHist.Message}");
            }

            // --- LÓGICA DE IMAGEM CORRIGIDA PARA BETA.15 ---
            if (!string.IsNullOrEmpty(imageBase64))
            {
                logger.LogInformation("Processando imagem para Beta.15...");

                // 1. Limpeza do Base64
                if (imageBase64.Contains(",")) imageBase64 = imageBase64.Split(',')[1];
                imageBase64 = imageBase64.Trim().Replace(" ", "+").Replace("\n", "").Replace("\r", "");

                // 2. Montar Data URI (O jeito que a Beta.15 aceita)
                // Formato: data:image/jpeg;base64,....
                string dataUrl = $"data:image/jpeg;base64,{imageBase64}";

                // 3. Criar mensagem usando URI (correção do erro CS1503)
                var imageItem = new ChatMessageImageContentItem(new Uri(dataUrl));
                var textItem = new ChatMessageTextContentItem(userPrompt);

                var multimodalMessage = new ChatRequestUserMessage(textItem, imageItem);
                options.Messages.Add(multimodalMessage);
            }
            else
            {
                options.Messages.Add(new ChatRequestUserMessage(userPrompt));
            }
            // -----------------------------------------------

            logger.LogInformation("Enviando requisição OpenAI...");
            var response = await aiClient.GetChatCompletionsAsync(options);
            string aiResponse = response.Value.Choices[0].Message.Content;

            // Salvar no Banco
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
            logger.LogError(ex, "FALHA GERAL");
            var errorRes = req.CreateResponse(System.Net.HttpStatusCode.InternalServerError);
            await errorRes.WriteStringAsync($"ERRO: {ex.Message}");
            return errorRes;
        }
    }
}

// Classes Auxiliares
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