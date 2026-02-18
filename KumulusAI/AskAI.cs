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
    private readonly IConfiguration _config;

    // 1. CONSTRUTOR LIMPO (Para não dar erro na inicialização)
    public AskAI(IConfiguration config)
    {
        _config = config;
    }

    [Function("AskAI")]
    public async Task<HttpResponseData> Run([HttpTrigger(AuthorizationLevel.Anonymous, "post")] HttpRequestData req)
    {
        // Logger recuperado do contexto (muito mais seguro que injeção)
        var logger = req.FunctionContext.GetLogger("AskAI");

        try
        {
            logger.LogInformation(">>> INICIANDO EXECUÇÃO SEGURA <<<");

            // 2. INICIALIZAÇÃO DENTRO DO TRY (Se falhar aqui, o erro aparece!)
            string endpoint = _config["AZURE_OPENAI_ENDPOINT"] ?? "";
            string key = _config["AZURE_OPENAI_KEY"] ?? "";
            string connectionString = _config["AzureWebJobsStorage"] ?? "";
            string deploymentName = _config["AZURE_OPENAI_DEPLOYMENT_NAME"] ?? "gpt-4o-mini";

            if (string.IsNullOrEmpty(endpoint)) throw new Exception("Endpoint da OpenAI está vazio ou nulo.");
            if (string.IsNullOrEmpty(key)) throw new Exception("Chave da OpenAI está vazia ou nula.");
            if (string.IsNullOrEmpty(connectionString)) throw new Exception("Connection String do Storage está vazia.");

            var aiClient = new OpenAIClient(new Uri(endpoint), new AzureKeyCredential(key));
            var tableClient = new TableClient(connectionString, "HistoricoConversas");
            await tableClient.CreateIfNotExistsAsync();

            // 3. LER O CORPO DA REQUISIÇÃO
            string requestBodyStr = await new StreamReader(req.Body).ReadToEndAsync();
            if (string.IsNullOrEmpty(requestBodyStr)) throw new Exception("O corpo da requisição chegou vazio.");

            // Desserialização manual para pegar erro de JSON
            PromptRequest? requestBody;
            try
            {
                requestBody = System.Text.Json.JsonSerializer.Deserialize<PromptRequest>(requestBodyStr, new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            catch (Exception jsonEx)
            {
                throw new Exception($"Erro ao ler JSON: {jsonEx.Message}");
            }

            string sessionId = requestBody?.SessionId ?? Guid.NewGuid().ToString();
            string userPrompt = string.IsNullOrWhiteSpace(requestBody?.Prompt) ? "Analise esta imagem" : requestBody.Prompt;
            string imageBase64 = requestBody?.ImageBase64 ?? "";

            var options = new ChatCompletionsOptions { DeploymentName = deploymentName, MaxTokens = 1000 };
            options.Messages.Add(new ChatRequestSystemMessage("Você é a LeIA. Responda em Markdown."));

            // 4. HISTÓRICO
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
                logger.LogWarning($"Não foi possível ler histórico: {exHist.Message}");
                // Não paramos o fluxo se o histórico falhar
            }

            // 5. PROCESSAR IMAGEM
            if (!string.IsNullOrEmpty(imageBase64))
            {
                logger.LogInformation("Processando imagem...");
                if (imageBase64.Contains(",")) imageBase64 = imageBase64.Split(',')[1];
                imageBase64 = imageBase64.Trim().Replace(" ", "+").Replace("\n", "").Replace("\r", "");

                byte[] imageBytes = Convert.FromBase64String(imageBase64);

                var multimodalMessage = new ChatRequestUserMessage(
                    new ChatMessageTextContentItem(userPrompt),
                    new ChatMessageImageContentItem(BinaryData.FromBytes(imageBytes), "image/jpeg")
                );
                options.Messages.Add(multimodalMessage);
            }
            else
            {
                options.Messages.Add(new ChatRequestUserMessage(userPrompt));
            }

            // 6. CHAMADA AI
            logger.LogInformation("Enviando para OpenAI...");
            var response = await aiClient.GetChatCompletionsAsync(options);
            string aiResponse = response.Value.Choices[0].Message.Content;

            // Salvar
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
            // AGORA O ERRO VAI APARECER!
            logger.LogError(ex, "ERRO CAPTURADO NO RUN");
            var errorRes = req.CreateResponse(System.Net.HttpStatusCode.InternalServerError);
            // Retorna texto puro para aparecer na aba "Response" do navegador
            await errorRes.WriteStringAsync($"ERRO CRÍTICO: {ex.Message} -> {ex.StackTrace}");
            return errorRes;
        }
    }
}

// ENTIDADES (Mantenha aqui para evitar conflito com outros arquivos)
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