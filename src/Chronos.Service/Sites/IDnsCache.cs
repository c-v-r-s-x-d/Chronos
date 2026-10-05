namespace Chronos.Service.Sites;

public interface IDnsCache
{
    /// <summary>Drops the resolver cache so a newly blocked name stops resolving at once. False when it could not be flushed.</summary>
    bool Flush();
}
