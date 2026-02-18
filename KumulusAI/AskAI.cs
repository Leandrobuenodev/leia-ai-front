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
            throw new Exception("ERRO: Configuração de Endpoint inválida ou não resolvida.");
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
            string userPrompt = string.IsNullOrWhiteSpace(requestBody?.Prompt) ? "Analise esta imagem" : requestBody.Prompt;
            string imageBase64 = requestBody?.ImageBase64 ?? "";

            var options = new ChatCompletionsOptions { DeploymentName = _deploymentName, MaxTokens = 1000 };
            options.Messages.Add(new ChatRequestSystemMessage("Você é a LeIA, assistente técnica da Kumulus. Você tem visão computacional e pode analisar imagens detalhadamente."));

            // 1. Recuperação de Histórico
            var history = _tableClient.QueryAsync<ChatHistoryEntity>(filter: $"PartitionKey eq '{sessionId}'");
            await foreach (var entity in history)
            {
                options.Messages.Add(new ChatRequestUserMessage(entity.UserMessage));
                options.Messages.Add(new ChatRequestAssistantMessage(entity.AIMessage));
            }

            // 2. Lógica Multimodal
            if (!string.IsNullOrEmpty(imageBase64))
            {
                // Limpeza do Base64
                if (imageBase64.Contains(",")) imageBase64 = imageBase64.Split(',')[1];
                imageBase64 = imageBase64.Trim().Replace("\n", "").Replace("\r", "");

                byte[] imageBytes = Convert.FromBase64String(imageBase64);

                var userMessageWithImage = new ChatRequestUserMessage(
                    new ChatMessageTextContentItem(userPrompt),
                    new ChatMessageImageContentItem(BinaryData.FromBytes(imageBytes), "image/jpeg")
                );
                options.Messages.Add(userMessageWithImage);
            }
            else
            {
                options.Messages.Add(new ChatRequestUserMessage(userPrompt));
            }

            // 3. Chamada à OpenAI
            var response = await _aiClient.GetChatCompletionsAsync(options);
            string aiResponse = response.Value.Choices[0].Message.Content;

            // 4. Persistência
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
            _logger.LogError(ex, "Erro no processamento da imagem ou texto.");
            var errorRes = req.CreateResponse(System.Net.HttpStatusCode.InternalServerError);
            await errorRes.WriteStringAsync($"ERRO TÉCNICO: {ex.Message}");
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