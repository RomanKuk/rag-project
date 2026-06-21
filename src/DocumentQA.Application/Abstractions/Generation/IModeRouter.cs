namespace DocumentQA.Application.Abstractions.Generation;

public interface IModeRouter
{
    bool ShouldUseAgent(string question);
}
