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
    private readonly ILogger<AskAI> _logger;

    public AskAI(IConfiguration config, ILogger<AskAI> logger)
    {
        _logger = logger;
        var endpoint = config["AZURE_OPENAI_ENDPOINT"];
        var key = config["AZURE_OPENAI_KEY"];
        _deploymentName = config["AZURE_OPENAI_DEPLOYMENT_NAME"] ?? "gpt-4o-mini";

        if (string.IsNullOrEmpty(endpoint) || !endpoint.StartsWith("http"))
        {
            throw new Exception("ERRO FATAL: Endpoint OpenAI inválido ou não configurado.");
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
            _logger.LogInformation("Iniciando processamento...");

            var requestBody = await req.ReadFromJsonAsync<PromptRequest>();
            string sessionId = requestBody?.SessionId ?? Guid.NewGuid().ToString();
            string userPrompt = string.IsNullOrWhiteSpace(requestBody?.Prompt) ? "Analise esta imagem" : requestBody.Prompt;
            string imageBase64 = requestBody?.ImageBase64 ?? "";

            var options = new ChatCompletionsOptions { DeploymentName = _deploymentName, MaxTokens = 1000 };
            options.Messages.Add(new ChatRequestSystemMessage("Você é a LeIA. Responda em Markdown."));

            // 1. Histórico (Usando a nova classe para evitar conflito)
            var history = _tableClient.QueryAsync<ChatLogEntity>(filter: $"PartitionKey eq '{sessionId}'");
            await foreach (var entity in history)
            {
                options.Messages.Add(new ChatRequestUserMessage(entity.UserMessage));
                options.Messages.Add(new ChatRequestAssistantMessage(entity.AIMessage));
            }

            // 2. Imagem
            if (!string.IsNullOrEmpty(imageBase64))
            {
                _logger.LogInformation("Imagem detectada. Iniciando conversão...");

                if (imageBase64.Contains(",")) imageBase64 = imageBase64.Split(',')[1];

                // Limpeza extra para evitar erro de 'Bad Format'
                imageBase64 = imageBase64.Trim().Replace(" ", "+").Replace("\n", "").Replace("\r", "");

                // O erro costuma acontecer AQUI
                byte[] imageBytes = Convert.FromBase64String(imageBase64);

                _logger.LogInformation($"Imagem convertida com sucesso: {imageBytes.Length} bytes.");

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

            var response = await _aiClient.GetChatCompletionsAsync(options);
            string aiResponse = response.Value.Choices[0].Message.Content;

            // 3. Salvar
            await _tableClient.AddEntityAsync(new ChatLogEntity
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
            // O ERRO VAI APARECER NO SEU CHAT AGORA
            _logger.LogError(ex, "ERRO NO ASKAI: {Message}", ex.Message);
            var errorRes = req.CreateResponse(System.Net.HttpStatusCode.InternalServerError);
            await errorRes.WriteStringAsync($"ERRO CRÍTICO: {ex.Message} | {ex.InnerException?.Message}");
            return errorRes;
        }
    }
}

// Classe renomeada para evitar conflito com arquivos antigos
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