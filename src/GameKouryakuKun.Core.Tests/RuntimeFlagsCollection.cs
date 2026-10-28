using Xunit;

namespace GameKouryakuKun.Core.Tests;

/// <summary>RuntimeFlags はプロセス共有の静的状態なので、触るテストは直列化する。</summary>
[CollectionDefinition("RuntimeFlagsSerial", DisableParallelization = true)]
public class RuntimeFlagsSerialDefinition { }
