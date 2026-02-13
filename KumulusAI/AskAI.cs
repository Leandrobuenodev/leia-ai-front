using Azure;
using Azure.AI.OpenAI;
using Azure.Data.Tables;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using System.Text.Json; // Usando a biblioteca padrão aqui

namespace KumulusAI;

public class AskAI
{
    private readonly OpenAIClient _aiClient;
    private readonly TableClient _tableClient;
    private readonly string _deploymentName;

    public AskAI(IConfiguration config)
    {
        _deploymentName = config["AZURE_OPENAI_DEPLOYMENT_NAME"] ?? "gpt-4o-mini";

        // Nomes das variáveis exatamente como estão no seu Portal Azure
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
        // Lê o JSON usando a biblioteca padrão do .NET
        var requestBody = await req.ReadFromJsonAsync<PromptRequest>();

        string sessionId = requestBody?.SessionId ?? Guid.NewGuid().ToString();
        string userPrompt = requestBody?.Prompt ?? "";
        string imageBase64 = requestBody?.ImageBase64 ?? "";

        var options = new ChatCompletionsOptions { DeploymentName = _deploymentName, MaxTokens = 800 };

        options.Messages.Add(new ChatRequestSystemMessage("Você é a LeIA, assistente técnica da Kumulus. Use Markdown."));

        // 1. HISTÓRICO
        var history = _tableClient.QueryAsync<ChatHistoryEntity>(filter: $"PartitionKey eq '{sessionId}'");
        await foreach (var entity in history)
        {
            options.Messages.Add(new ChatRequestUserMessage(entity.UserMessage));
            options.Messages.Add(new ChatRequestAssistantMessage(entity.AIMessage));
        }

        // 2. LÓGICA DE IMAGEM (Voltando para URI que sua versão suporta)
        if (!string.IsNullOrEmpty(imageBase64))
        {
            // Garante que a string base64 esteja limpa
            if (imageBase64.Contains(",")) imageBase64 = imageBase64.Split(',')[1];

            // Cria a URI no formato de dados que a IA aceita
            var imageUri = new Uri($"data:image/jpeg;base64,{imageBase64}");

            var multimodalContent = new List<ChatMessageContentItem>
            {
                new ChatMessageTextContentItem(userPrompt),
                new ChatMessageImageContentItem(imageUri) // Aqui não dá mais erro!
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
            ChatTitle = "Nova Conversa"
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