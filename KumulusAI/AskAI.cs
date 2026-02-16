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
        // Pega os nomes EXATOS que estão no seu Portal Azure
        _deploymentName = config["AZURE_OPENAI_DEPLOYMENT_NAME"] ?? "gpt-4o-mini";
        _aiClient = new OpenAIClient(
            new Uri(config["AZURE_OPENAI_ENDPOINT"]!),
            new AzureKeyCredential(config["AZURE_OPENAI_KEY"]!)
        );
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

        var options = new ChatCompletionsOptions { DeploymentName = _deploymentName, MaxTokens = 800 };
        options.Messages.Add(new ChatRequestSystemMessage("Você é a LeIA, assistente técnica da Kumulus. Use Markdown."));

        // 1. RECUPERAÇÃO DE HISTÓRICO
        var history = _tableClient.QueryAsync<ChatHistoryEntity>(filter: $"PartitionKey eq '{sessionId}'");
        await foreach (var entity in history)
        {
            options.Messages.Add(new ChatRequestUserMessage(entity.UserMessage));
            options.Messages.Add(new ChatRequestAssistantMessage(entity.AIMessage));
        }

        // 2. LÓGICA DE IMAGEM (Limpa e envia)
        if (!string.IsNullOrEmpty(imageBase64))
        {
            // Remove o prefixo "data:image/..." caso ele venha do front-end
            if (imageBase64.Contains(",")) imageBase64 = imageBase64.Split(',')[1];

            // Criando a URI de dados de forma segura
            var imageUri = new Uri($"data:image/jpeg;base64,{imageBase64}");

            var multimodalContent = new List<ChatMessageContentItem>
            {
                new ChatMessageTextContentItem(userPrompt),
                new ChatMessageImageContentItem(imageUri)
            };
            options.Messages.Add(new ChatRequestUserMessage(multimodalContent));
        }
        else
        {
            options.Messages.Add(new ChatRequestUserMessage(userPrompt));
        }

        // 3. CHAMADA À IA
        var response = await _aiClient.GetChatCompletionsAsync(options);
        string aiResponse = response.Value.Choices[0].Message.Content;

        // 4. PERSISTÊNCIA E RESPOSTA
        await _tableClient.AddEntityAsync(new ChatHistoryEntity
        {
            PartitionKey = sessionId,
            RowKey = DateTime.UtcNow.Ticks.ToString(),
            UserMessage = userPrompt,
            AIMessage = aiResponse,
            ChatTitle = "Conversa com Imagem"
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