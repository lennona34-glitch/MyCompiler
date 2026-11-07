namespace MyCompiler.Models;

public sealed class AiSettings
{
    public string ApiUrl { get; set; } = "https://api.openai.com/v1/chat/completions";
    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "gpt-4o";
    public string Provider { get; set; } = "OpenAI";
    public double Temperature { get; set; } = 0.2;
    public int MaxTokens { get; set; } = 4096;
    public string SystemPrompt { get; set; } = DefaultSystemPrompt;

    public const string DefaultSystemPrompt = 
        "You are MyCompiler AI Studio, an expert software architect, systems programmer, and UI designer integrated into the MyCompiler IDE. " +
        "Your capabilities include refactoring open source code, revamping UI styles (e.g. modern neon palettes, dark mode, typography), " +
        "optimizing algorithms, fixing compiler errors, and preparing clean pull requests. " +
        "Always provide clean, working, production-ready code. Wrap code snippets in standard markdown code fences (e.g. ```csharp ... ``` or ```xaml ... ```). " +
        "When rewriting an existing file or selection, prefer returning the complete updated snippet so it can be applied directly to the editor.";

    public static readonly IReadOnlyList<AiProviderPreset> Presets =
    [
        new("Google Gemini", "https://generativelanguage.googleapis.com/v1beta/openai/chat/completions", "gemini-3.5-flash", true, "Google Gemini via AI Studio (Generous Free tier — no credit card needed)", "https://aistudio.google.com/apikey", "GEMINI_API_KEY", ["gemini-3.5-flash", "gemini-3.5-flash-lite", "gemini-flash-lite-latest"]),
        new("OpenAI", "https://api.openai.com/v1/chat/completions", "gpt-4o", true, "Official OpenAI API (requires OpenAI API key)", "https://platform.openai.com/api-keys", "OPENAI_API_KEY", ["gpt-4o", "gpt-4o-mini", "o3-mini"]),
        new("OpenRouter", "https://openrouter.ai/api/v1/chat/completions", "anthropic/claude-3.5-sonnet", true, "Universal router (Claude, DeepSeek, LLaMA, etc.)", "https://openrouter.ai/keys", "OPENROUTER_API_KEY", ["anthropic/claude-3.5-sonnet", "deepseek/deepseek-r1", "meta-llama/llama-3.3-70b-instruct"]),
        new("DeepSeek", "https://api.deepseek.com/chat/completions", "deepseek-chat", true, "DeepSeek V3 / R1 reasoning & coding models", "https://platform.deepseek.com/api_keys", "DEEPSEEK_API_KEY", ["deepseek-chat", "deepseek-reasoner"]),
        new("Groq (Ultra-Fast)", "https://api.groq.com/openai/v1/chat/completions", "llama-3.3-70b-versatile", true, "Sub-second lightning-fast LLaMA inference", "https://console.groq.com/keys", "GROQ_API_KEY", ["llama-3.3-70b-versatile", "llama-3.1-8b-instant"]),
        new("Ollama (Local & Free)", "http://localhost:11434/v1/chat/completions", "qwen2.5-coder", false, "Runs 100% locally & offline on your PC (zero API key needed)", "https://ollama.com/download", null, ["qwen2.5-coder", "deepseek-r1:14b", "llama3.2"]),
        new("LM Studio (Local)", "http://localhost:1234/v1/chat/completions", "local-model", false, "Local desktop LLM server (zero API key needed)", "https://lmstudio.ai", null, ["local-model"]),
        new("Custom", "", "", false, "Custom OpenAI-compatible endpoint")
    ];
}

public sealed record AiProviderPreset(
    string Name,
    string DefaultUrl,
    string DefaultModel,
    bool RequiresApiKey,
    string Description,
    string? ApiKeyUrl = null,
    string? EnvVarName = null,
    IReadOnlyList<string>? SuggestedModels = null);

public sealed record AiPromptContext(
    string FileName,
    string Language,
    string SourceCode,
    string? SelectedCode,
    string? BuildOutput,
    string? WorkspaceRoot);
