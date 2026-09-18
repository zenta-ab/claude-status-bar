using Xunit;

namespace ClaudeStatusBar.Tests;

/// <summary>
/// Integration tests that spawn real FakeClaudeChild processes share one collection so xUnit runs
/// them one at a time. They are timing-sensitive (10 s waits for a poll to land), and
/// ChannelSupervisorTests asserts globally that no FakeClaudeChild survives a shutdown -- an
/// assertion another class's children would break if it ran concurrently.
/// </summary>
[CollectionDefinition("process", DisableParallelization = true)]
public class ProcessTestCollection { }
