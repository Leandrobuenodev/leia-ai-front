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

    public AskAI(IConfiguration config)
    {
        var endpoint = config["AZURE_OPENAI_ENDPOINT"];
        var key = config["AZURE_OPENAI_KEY"];
        _deploymentName = config["AZURE_OPENAI_DEPLOYMENT_NAME"] ?? "gpt-4o-mini";

        // Validação básica para o ambiente local (local.settings.json)
        if (string.IsNullOrEmpty(endpoint) || !endpoint.StartsWith("http"))
        {
            throw new Exception("ERRO: Endpoint não configurado corretamente.");
        }

        _aiClient = new OpenAIClient(new Uri(endpoint), new AzureKeyCredential(key!));
        _tableClient = new TableClient(config["AzureWebJobsStorage"]!, "HistoricoConversas");
        _tableClient.CreateIfNotExists();
    }

    [Function("AskAI")]
    public async Task<HttpResponseData> Run([HttpTrigger(AuthorizationLevel.Anonymous, "post")] HttpRequestData req)
    {
        var requestBody = await req.ReadFromJsonAsync<PromptRequest>();
        string sessionId = requestBody?.SessionId ?? Guid.NewGuid().ToString();
        string userPrompt = requestBody?.Prompt ?? "";
        string imageBase64 = requestBody?.ImageBase64 ?? "";

        var options = new ChatCompletionsOptions { DeploymentName = _deploymentName, MaxTokens = 1000 };
        options.Messages.Add(new ChatRequestSystemMessage("Você é a LeIA, assistente técnica da Kumulus. Responda em Markdown."));

        // Recupera histórico usando a entidade já definida no seu projeto
        var history = _tableClient.QueryAsync<ChatHistoryEntity>(filter: $"PartitionKey eq '{sessionId}'");
        await foreach (var entity in history)
        {
            options.Messages.Add(new ChatRequestUserMessage(entity.UserMessage));
            options.Messages.Add(new ChatRequestAssistantMessage(entity.AIMessage));
        }

        // Processamento de Imagem
        if (!string.IsNullOrEmpty(imageBase64))
        {
            if (imageBase64.Contains(",")) imageBase64 = imageBase64.Split(',')[1];

            var multimodalContent = new List<ChatMessageContentItem>
            {
                new ChatMessageTextContentItem(userPrompt),
                new ChatMessageImageContentItem(new Uri($"data:image/jpeg;base64,{imageBase64}"))
            };
            options.Messages.Add(new ChatRequestUserMessage(multimodalContent));
        }
        else
        {
            options.Messages.Add(new ChatRequestUserMessage(userPrompt));
        }

        var response = await _aiClient.GetChatCompletionsAsync(options);
        string aiResponse = response.Value.Choices[0].Message.Content;

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
}

public class PromptRequest
{
    public string? Prompt { get; set; }
    public string? SessionId { get; set; }
    public string? ImageBase64 { get; set; }
}