namespace ContractScribe.Agent.Providers;

public enum OpenAiCompatibleThinkingMode
{
    Enabled,
    Disabled,
}

public enum OpenAiCompatibleReasoningEffort
{
    High,
}

public enum OpenAiCompatibleToolChoice
{
    Omitted,
    Auto,
    Required,
}

public enum OpenAiCompatibleContinuationPolicy
{
    Optional,
    RequiredForToolCalls,
}

public enum OpenAiCompatibleOutputTokenField
{
    MaxTokens,
    MaxCompletionTokens,
}

public sealed class OpenAiCompatibleChatCompletionsRequestProfile
{
    public OpenAiCompatibleChatCompletionsRequestProfile(
        OpenAiCompatibleThinkingMode thinkingMode,
        OpenAiCompatibleReasoningEffort? reasoningEffort,
        OpenAiCompatibleToolChoice toolChoice,
        OpenAiCompatibleContinuationPolicy continuationPolicy,
        OpenAiCompatibleOutputTokenField outputTokenField)
    {
        if (!Enum.IsDefined(thinkingMode)
            || reasoningEffort is { } effort && !Enum.IsDefined(effort)
            || !Enum.IsDefined(toolChoice)
            || !Enum.IsDefined(continuationPolicy)
            || !Enum.IsDefined(outputTokenField)
            || thinkingMode == OpenAiCompatibleThinkingMode.Disabled && reasoningEffort is not null)
        {
            throw new ArgumentException("The request profile is outside the selected transport boundary.");
        }

        ThinkingMode = thinkingMode;
        ReasoningEffort = reasoningEffort;
        ToolChoice = toolChoice;
        ContinuationPolicy = continuationPolicy;
        OutputTokenField = outputTokenField;
    }

    public OpenAiCompatibleThinkingMode ThinkingMode { get; }

    public OpenAiCompatibleReasoningEffort? ReasoningEffort { get; }

    public OpenAiCompatibleToolChoice ToolChoice { get; }

    public OpenAiCompatibleContinuationPolicy ContinuationPolicy { get; }

    public OpenAiCompatibleOutputTokenField OutputTokenField { get; }

    public override string ToString() => nameof(OpenAiCompatibleChatCompletionsRequestProfile);
}
