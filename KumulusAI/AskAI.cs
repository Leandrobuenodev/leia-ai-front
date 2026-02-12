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
        string imageBase64 = requestBody?.ImageBase64 ?? "";

        var options = new ChatCompletionsOptions { DeploymentName = _deploymentName, MaxTokens = 800 };

        // Persona da LeIA configurada conforme pedido do PO
        options.Messages.Add(new ChatRequestSystemMessage("Você é a LeIA, uma assistente técnica da Kumulus especializada em Azure e IA. Seja prestativa, profissional e use Markdown para formatar a resposta."));

        // 1. RECUPERAÇÃO DE HISTÓRICO (Apenas texto)
        var history = _tableClient.QueryAsync<ChatHistoryEntity>(filter: $"PartitionKey eq '{sessionId}'");
        await foreach (var entity in history)
        {
            options.Messages.Add(new ChatRequestUserMessage(entity.UserMessage));
            options.Messages.Add(new ChatRequestAssistantMessage(entity.AIMessage));
        }

        // 2. LÓGICA MULTIMODAL (Decide se envia imagem ou não)
        if (!string.IsNullOrEmpty(imageBase64))
        {
            // Se houver imagem, cria lista com Texto + Imagem
            var multimodalContent = new List<ChatMessageContentItem>
            {
                new ChatMessageTextContentItem(userPrompt),
                new ChatMessageImageContentItem(new Uri($"data:image/jpeg;base64,{imageBase64}"))
            };
            options.Messages.Add(new ChatRequestUserMessage(multimodalContent));
        }
        else
        {
            // Se não houver imagem, envia apenas o texto
            options.Messages.Add(new ChatRequestUserMessage(userPrompt));
        }

        // 3. CHAMADA À IA
        var response = await _aiClient.GetChatCompletionsAsync(options);
        string aiResponse = response.Value.Choices[0].Message.Content;

        // 4. GERAÇÃO DE TÍTULO (Se for o início da conversa)
        string title = "Conversa Antiga";
        if (options.Messages.Count <= 2)
        {
            var tOpt = new ChatCompletionsOptions { DeploymentName = _deploymentName, MaxTokens = 10 };
            tOpt.Messages.Add(new ChatRequestSystemMessage("Resuma em 3 palavras sem aspas."));
            tOpt.Messages.Add(new ChatRequestUserMessage(userPrompt));
            var tRes = await _aiClient.GetChatCompletionsAsync(tOpt);
            title = tRes.Value.Choices[0].Message.Content.Trim();
        }

        // 5. PERSISTÊNCIA NO BANCO (Salvando apenas o texto para economizar espaço)
        await _tableClient.AddEntityAsync(new ChatHistoryEntity
        {
            PartitionKey = sessionId,
            RowKey = DateTime.UtcNow.Ticks.ToString(),
            UserMessage = userPrompt,
            AIMessage = aiResponse,
            ChatTitle = title
        });

        // 6. RESPOSTA PARA O FRONTEND
        var res = req.CreateResponse(System.Net.HttpStatusCode.OK);
        await res.WriteAsJsonAsync(new { answer = aiResponse, sessionId = sessionId });
        return res;
    }
}

// DTO para receber dados do Frontend
public class PromptRequest
{
    public string? Prompt { get; set; }
    public string? SessionId { get; set; }
    public string? ImageBase64 { get; set; }
}
