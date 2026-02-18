using Azure;
using Azure.AI.OpenAI;
using Azure.Data.Tables;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;

namespace KumulusAI;

public class AskAI
{
    private readonly OpenAIClient _aiClient;
    private readonly TableClient _tableClient;
    private readonly string _deploymentName;
    private readonly ILogger _logger;

    public AskAI(IConfiguration config, ILoggerFactory loggerFactory)
    {
        _logger = loggerFactory.CreateLogger<AskAI>();
        var endpoint = config["AZURE_OPENAI_ENDPOINT"];
        var key = config["AZURE_OPENAI_KEY"];
        _deploymentName = config["AZURE_OPENAI_DEPLOYMENT_NAME"] ?? "gpt-4o-mini";

        if (string.IsNullOrEmpty(endpoint) || !endpoint.StartsWith("http"))
        {
            throw new Exception("ERRO: Endpoint OpenAI não configurado ou inválido.");
        }

        _aiClient = new OpenAIClient(new Uri(endpoint), new AzureKeyCredential(key!));
        _tableClient = new TableClient(config["AzureWebJobsStorage"]!, "HistoricoConversas");
        _tableClient.CreateIfNotExists();
    }

    [Function("AskAI")]
    public async Task<HttpResponseData> Run([HttpTrigger(AuthorizationLevel.Anonymous, "post")] HttpRequestData req)
    {
        try
        {
            var requestBody = await req.ReadFromJsonAsync<PromptRequest>();
            string sessionId = requestBody?.SessionId ?? Guid.NewGuid().ToString();
            string userPrompt = requestBody?.Prompt ?? "";
            string imageBase64 = requestBody?.ImageBase64 ?? "";

            var options = new ChatCompletionsOptions { DeploymentName = _deploymentName, MaxTokens = 1000 };
            options.Messages.Add(new ChatRequestSystemMessage("Você é a LeIA, assistente técnica da Kumulus. Responda em Markdown."));

            // 1. Recupera histórico
            var history = _tableClient.QueryAsync<ChatHistoryEntity>(filter: $"PartitionKey eq '{sessionId}'");
            await foreach (var entity in history)
            {
                options.Messages.Add(new ChatRequestUserMessage(entity.UserMessage));
                options.Messages.Add(new ChatRequestAssistantMessage(entity.AIMessage));
            }

            // 2. Processamento Multimodal (O FIX ESTÁ AQUI)
            if (!string.IsNullOrEmpty(imageBase64))
            {
                if (imageBase64.Contains(",")) imageBase64 = imageBase64.Split(',')[1];
                byte[] imageBytes = Convert.FromBase64String(imageBase64);

                var multimodalContent = new List<ChatMessageContentItem>
                {
                    new ChatMessageTextContentItem(userPrompt),
                    new ChatMessageImageContentItem(BinaryData.FromBytes(imageBytes), "image/jpeg")
                };
                options.Messages.Add(new ChatRequestUserMessage(multimodalContent));
            }
            else
            {
                // FIX: Adiciona o prompt mesmo quando NÃO há imagem
                options.Messages.Add(new ChatRequestUserMessage(userPrompt));
            }

            // 3. Chamada à IA
            var response = await _aiClient.GetChatCompletionsAsync(options);
            string aiResponse = response.Value.Choices[0].Message.Content;

            // 4. Salva histórico
            await _tableClient.AddEntityAsync(new ChatHistoryEntity
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
            _logger.LogError(ex, "Erro no AskAI");
            var errorRes = req.CreateResponse(System.Net.HttpStatusCode.InternalServerError);
            // Retorna o erro real para o chat para facilitar o seu debug
            await errorRes.WriteStringAsync($"Erro técnico: {ex.Message}");
            return errorRes;
        }
    }
}

public class PromptRequest
{
    public string? Prompt { get; set; }
    public string? SessionId { get; set; }
    public string? ImageBase64 { get; set; }
}