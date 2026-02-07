using Azure;
using Azure.Data.Tables;

namespace KumulusAI;

public class ChatHistoryEntity : ITableEntity
{
    public string PartitionKey { get; set; } = default!; 
    public string RowKey { get; set; } = default!;      
    public DateTimeOffset? Timestamp { get; set; }
    public ETag ETag { get; set; }
    public string UserMessage { get; set; } = default!;
    public string AIMessage { get; set; } = default!;
    public string? ChatTitle { get; set; } 
}