namespace TestForPromoOS.Messaging;

public interface ITaskEventPublisher
{
    void PublishTaskCompleted(TaskCompletedMessage message);
}
