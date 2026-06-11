namespace DocumentQA.Application.Abstractions.Generation;

public interface IModelRouter
{
    string[] Route(string question, string[] defaultChain);
}
