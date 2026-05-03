namespace Bitwyck.Core.Interfaces;

public interface IToolRegistry
{
    void Register(ITool tool);
    bool TryGet(string name, out ITool? tool);
    IReadOnlyCollection<ITool> All();
    string ToPromptManifest();
}
