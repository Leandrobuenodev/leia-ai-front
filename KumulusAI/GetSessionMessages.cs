using Azure.Data.Tables;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using System.Net;

namespace KumulusAI;

public class GetSessionMessages
{
    private readonly TableClient _tableClient;
    private readonly ILogger _logger;

    public GetSessionMessages(ILoggerFactory loggerFactory, IConfiguration config)
    {
        _logger = loggerFactory.CreateLogger<GetSessionMessages>();
        _tableClient = new TableClient(config["STORAGE_CONNECTION_STRING"] ?? "", "HistoricoConversas");
    }

    [Function("GetSessionMessages")]
    public async Task<HttpResponseData> Run([HttpTrigger(AuthorizationLevel.Anonymous, "get")] HttpRequestData req)
    {
        // Pega o sessionId da URL (ex: ?sessionId=123)
        var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
        string? sessionId = query["sessionId"];

        if (string.IsNullOrEmpty(sessionId))
        {
            return req.CreateResponse(HttpStatusCode.BadRequest);
        }

        _logger.LogInformation($"Recuperando histórico completo da sessão: {sessionId}");

        // Busca todas as linhas que pertencem a essa sessão
        var entities = _tableClient.QueryAsync<ChatHistoryEntity>(filter: $"PartitionKey eq '{sessionId}'");
        
        var messages = new List<object>();

        await foreach (var entity in entities)
        {
            messages.Add(new { 
                userMessage = entity.UserMessage, 
                aiMessage = entity.AIMessage,
                timestamp = entity.Timestamp 
            });
        }

        var response = req.CreateResponse(HttpStatusCode.OK);
        // Retorna a lista completa de balões para o front-end
        await response.WriteAsJsonAsync(messages);
        return response;
    }
}