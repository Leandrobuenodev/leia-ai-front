using Azure.Data.Tables;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;

namespace KumulusAI;

public class GetHistory
{
    private readonly TableClient _tableClient;
    private readonly ILogger _logger;

    public GetHistory(ILoggerFactory loggerFactory, IConfiguration config)
    {
        _logger = loggerFactory.CreateLogger<GetHistory>();
        string connectionString = config["AzureWebJobsStorage"] ?? "";
        _tableClient = new TableClient(connectionString, "HistoricoConversas");
    }

    [Function("GetHistory")]
public async Task<HttpResponseData> Run([HttpTrigger(AuthorizationLevel.Anonymous, "get")] HttpRequestData req)
{
    // Busca todas as entidades
    var entities = _tableClient.QueryAsync<ChatHistoryEntity>();
    
    // Agrupa por SessionId e pega o Título e a data da última mensagem para ordenar
    var sessions = new List<ChatHistoryEntity>();
    await foreach (var entity in entities) {
        sessions.Add(entity);
    }

    var result = sessions
        .GroupBy(s => s.PartitionKey)
        .Select(g => new { 
            id = g.Key, 
            title = g.First().ChatTitle ?? "Nova Conversa",
            lastUpdate = g.Max(x => x.Timestamp) // Para ordenar os recentes primeiro
        })
        .OrderByDescending(x => x.lastUpdate);

    var response = req.CreateResponse(System.Net.HttpStatusCode.OK);
    await response.WriteAsJsonAsync(result);
    return response;
}
}