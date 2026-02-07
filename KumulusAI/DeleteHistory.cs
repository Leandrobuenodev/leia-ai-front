using Azure.Data.Tables;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using System.Net;

namespace KumulusAI;

public class DeleteHistory
{
    private readonly TableClient _tableClient;
    private readonly ILogger _logger;

    public DeleteHistory(ILoggerFactory loggerFactory, IConfiguration config)
    {
        _logger = loggerFactory.CreateLogger<DeleteHistory>();
        // Conecta na mesma tabela onde você salva o histórico
        _tableClient = new TableClient(config["AzureWebJobsStorage"] ?? "", "HistoricoConversas");
    }

    [Function("DeleteHistory")]
    public async Task<HttpResponseData> Run([HttpTrigger(AuthorizationLevel.Anonymous, "delete")] HttpRequestData req)
    {
        // Pega o sessionId da URL (?sessionId=xxxx)
        var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
        string? sessionId = query["sessionId"];

        if (string.IsNullOrEmpty(sessionId))
        {
            return req.CreateResponse(HttpStatusCode.BadRequest);
        }

        _logger.LogInformation($"Iniciando exclusão da sessão: {sessionId}");

        try
        {
            // No Table Storage, precisamos deletar cada linha (mensagem) da conversa
            // O PartitionKey é o seu sessionId
            var entities = _tableClient.QueryAsync<ChatHistoryEntity>(filter: $"PartitionKey eq '{sessionId}'");
            
            int count = 0;
            await foreach (var entity in entities)
            {
                await _tableClient.DeleteEntityAsync(entity.PartitionKey, entity.RowKey);
                count++;
            }

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteStringAsync($"Sucesso: {count} mensagens removidas do banco.");
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError($"Erro ao deletar: {ex.Message}");
            return req.CreateResponse(HttpStatusCode.InternalServerError);
        }
    }
}