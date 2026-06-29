public sealed class AuthoritativeActionFeedbackSystem : IDisposable
{
    private readonly IDisposable _rejectionSubscription;
    private float _remainingSeconds;

    public AuthoritativeActionFeedbackSystem(SceneEventHub events, float displayDurationSeconds = 2.5f)
    {
        DisplayDurationSeconds = Math.Max(0.1f, displayDurationSeconds);
        _rejectionSubscription = events.Subscribe<AuthoritativeActionRejectedEvent>(OnRejected);
    }

    public float DisplayDurationSeconds { get; }

    public string? CurrentRejectionMessage { get; private set; }

    public void Update(float deltaTime)
    {
        if (string.IsNullOrWhiteSpace(CurrentRejectionMessage))
        {
            return;
        }

        _remainingSeconds = Math.Max(0f, _remainingSeconds - Math.Max(0f, deltaTime));
        if (_remainingSeconds <= 0f)
        {
            CurrentRejectionMessage = null;
        }
    }

    public void Dispose()
    {
        _rejectionSubscription.Dispose();
    }

    private void OnRejected(AuthoritativeActionRejectedEvent evt)
    {
        CurrentRejectionMessage = evt.Result.Rejection?.Reason;
        _remainingSeconds = DisplayDurationSeconds;
    }
}
