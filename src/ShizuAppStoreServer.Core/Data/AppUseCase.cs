namespace ShizuAppStoreServer.Core.Data;

/// <summary>
/// Join row between an app and a use case tag (table <c>app_use_cases</c>).
/// Rewritten in full whenever tagging for an app succeeds, so capabilities
/// that disappear from the report disappear from the catalog too.
/// </summary>
public sealed class AppUseCase
{
    public long AppId { get; set; }
    public App? App { get; set; }

    public long UseCaseId { get; set; }
    public UseCase? UseCase { get; set; }
}
