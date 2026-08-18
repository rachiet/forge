namespace Forge.Core.Llm;

/// <summary>
/// How much model a job deserves — the vocabulary the spec already used ("coding
/// tier", "high-reasoning tier") made into a type.
///
/// A recipe names a tier, never a model id. That split keeps orchestration policy
/// (an engineer needs the coding tier) separate from provider knowledge (what the
/// coding tier is called at Anthropic today), so adding a role stays a record plus a
/// prompt file, and adding a provider stays one class with two strings in it.
/// Neither side has to learn the other's vocabulary.
/// </summary>
public enum ModelTier
{
    /// <summary>The working tier: writing code, verifying behaviour against a contract.</summary>
    Coding,

    /// <summary>The strongest available. Design, review, and rescuing stuck work.</summary>
    Reasoning,
}
