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
        _deploymentName = config["AZURE_OPENAI_DEPLOYMENT_NAME"] ?? "gpt-4o-mini";
        _aiClient = new OpenAIClient(new Uri(config["AZURE_OPENAI_ENDPOINT"]!), new AzureKeyCredential(config["AZURE_OPENAI_KEY"]!));
        _tableClient = new TableClient(config["AzureWebJobsStorage"]!, "HistoricoConversas");
        _tableClient.CreateIfNotExists();
    }

    [Function("AskAI")]
    public async Task<HttpResponseData> Run([HttpTrigger(AuthorizationLevel.Anonymous, "post")] HttpRequestData req)
    {
        var requestBody = await req.ReadFromJsonAsync<PromptRequest>();
        string sessionId = requestBody?.SessionId ?? Guid.NewGuid().ToString();
        string userPrompt = requestBody?.Prompt ?? "";

        var options = new ChatCompletionsOptions { DeploymentName = _deploymentName, MaxTokens = 800 };
        options.Messages.Add(new ChatRequestSystemMessage("Você é a LeIA. Use Markdown para formatar a resposta (negritos, listas e código)."));

        // BUSCA HISTÓRICO REAL NO BANCO
        var history = _tableClient.QueryAsync<ChatHistoryEntity>(filter: $"PartitionKey eq '{sessionId}'");
        await foreach (var entity in history)
        {
            options.Messages.Add(new ChatRequestUserMessage(entity.UserMessage));
            options.Messages.Add(new ChatRequestAssistantMessage(entity.AIMessage));
        }

        options.Messages.Add(new ChatRequestUserMessage(userPrompt));
        var response = await _aiClient.GetChatCompletionsAsync(options);
        string aiResponse = response.Value.Choices[0].Message.Content;

        // TÍTULO INTELIGENTE
        string title = "Conversa Antiga";
        if (options.Messages.Count <= 2) {
            var tOpt = new ChatCompletionsOptions { DeploymentName = _deploymentName, MaxTokens = 10 };
            tOpt.Messages.Add(new ChatRequestSystemMessage("Resuma em 3 palavras sem aspas."));
            tOpt.Messages.Add(new ChatRequestUserMessage(userPrompt));
            var tRes = await _aiClient.GetChatCompletionsAsync(tOpt);
            title = tRes.Value.Choices[0].Message.Content.Trim();
        }

        // SALVA NO BANCO
        await _tableClient.AddEntityAsync(new ChatHistoryEntity {
            PartitionKey = sessionId,
            RowKey = DateTime.UtcNow.Ticks.ToString(),
            UserMessage = userPrompt,
            AIMessage = aiResponse,
            ChatTitle = title
        });

        var res = req.CreateResponse(System.Net.HttpStatusCode.OK);
        await res.WriteAsJsonAsync(new { answer = aiResponse, sessionId = sessionId });
        return res;
    }
}
public class PromptRequest { public string? Prompt { get; set; } public string? SessionId { get; set; } }