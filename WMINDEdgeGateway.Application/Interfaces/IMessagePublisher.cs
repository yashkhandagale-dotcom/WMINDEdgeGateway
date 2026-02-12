namespace WMINDEdgeGateway.Application.Interfaces;

public interface IMessagePublisher
{
    Task PublishBatchAsync<T>(
        IEnumerable<T> messages,
        CancellationToken cancellationToken
    );
}
