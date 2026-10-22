# StabilityHarness 🌀 - 高性能AI代理稳定性监控库

## 项目概述

StabilityHarness 是一个专为多轮LLM代理设计的Lyapunov稳定性监控C#库。通过实时监控token消耗的能量函数导数ΔV，检测和分类代理执行中的失控模式（上下文螺旋、重试风暴、策略漂移等），在无需额外LLM调用的情况下提供诊断和建议。

## 核心技术规格

### 目标平台
- **.NET版本**: .NET 10.0+
- **目标框架**: `net10.0`（主库）, `netstandard2.1`（兼容层可选）
- **运行平台**: Windows x64, Linux x64, macOS ARM64/Intel
- **CPU架构支持**: x86-64 (AVX2/AVX-512), ARM64 (NEON/Adv SIMD)

### 性能指标
- Lyapunov监控: < 1μs/步（含硬件加速）
- RG文本处理: < 100μs/压缩（100条消息）
- VSA向量运算: < 10μs/检查（10K维向量）
- 内存占用: < 1MB/监控实例，零碎片分配

## 架构设计

### 项目结构
```
StabilityHarness/
├── StabilityHarness.csproj              # 主项目文件
├── StabilityHarness.Core/               # 核心算法实现
│   ├── Lyapunov/                       # Lyapunov稳定性监控
│   │   ├── LyapunovMonitor.cs          # 核心监控器（线程安全）
│   │   ├── GrowthRatioGuard.cs         # 增长比率守卫（推荐）
│   │   ├── StabilityStatus.cs          # 稳定性状态枚举
│   │   ├── StabilityException.cs       # 异常体系
│   │   ├── StabilityConfig.cs          # 配置类
│   │   └── StabilityResult.cs          # 监控结果
│   ├── RGDecimator/                    # 重整化组文本压缩
│   │   ├── RGDecimator.cs              # 文本粗粒度引擎
│   │   ├── ScoredMessage.cs            # 评分消息
│   │   ├── TextProcessor.cs            # 高性能文本处理
│   │   ├── KeywordsSet.cs              # 关键词集合（SIMD优化）
│   │   └── RGConfig.cs                 # RG配置
│   ├── VSA/                            # 向量符号架构
│   │   ├── HolographicEngine.cs        # 全息引擎
│   │   ├── VsaCore.cs                  # VSA核心（泛型维度）
│   │   ├── VectorOperations.cs         # 向量运算抽象
│   │   ├── HyperVector.cs              # 超向量类型
│   │   └── VSAConfig.cs                # VSA配置
│   ├── Diagnostics/                    # 诊断报告系统
│   │   ├── FailureReport.cs            # 失败报告
│   │   ├── FailurePattern.cs           # 失败模式枚举
│   │   ├── Suggestion.cs               # 建议类
│   │   ├── CostEstimator.cs            # 成本估算
│   │   ├── PatternClassifier.cs        # 模式分类器
│   │   └── EvidenceBuilder.cs          # 证据构建器
│   ├── Utils/                          # 工具类
│   │   ├── HardwareAccelerator.cs      # 硬件加速检测
│   │   ├── MemoryPoolManager.cs        # 内存池管理
│   │   ├── SpanExtensions.cs           # Span扩展方法
│   │   ├── ThreadSafe.cs               # 线程安全工具
│   │   ├── FastRandom.cs               # 高性能随机数
│   │   └── HashFunctions.cs            # 哈希函数
│   └── StabilityHarness.cs             # 库主入口类
├── tests/                              # 测试项目
│   ├── StabilityHarness.Tests.csproj
│   ├── LyapunovTests/
│   ├── RGDecimatorTests/
│   ├── VSATests/
│   ├── DiagnosticsTests/
│   └── IntegrationTests/
├── benchmarks/                         # 性能基准
│   ├── StabilityHarness.Benchmarks.csproj
│   ├── LyapunovBenchmarks.cs
│   ├── VSABenchmarks.cs
│   ├── RGDecimatorBenchmarks.cs
│   └── MemoryBenchmarks.cs
└── samples/                            # 示例代码
    ├── BasicUsage/
    ├── CliIntegration/
    └── WebApiIntegration/
```

## 核心算法规范

### 1. Lyapunov稳定性监控

#### 数学模型
```
V(k) = S(k) + λ·θ(k)
ΔV(k) = V(k) - V(k-1)

其中:
  S(k): 第k步的瞬时token消耗
  θ(k): 第k步的错误/重试权重
  λ: 耦合常数（默认1.0）
```

#### 稳定性判断规则
```csharp
// 状态机
enum StabilityStatus
{
    Stable,     // ΔV < 0，系统收敛
    Warning,    // ΔV ≥ 0，但 < window
    Unstable,   // ΔV ≥ 0 持续 window 步
    Tripped,    // 电路中断器已触发
    Frozen      // 预算耗尽
}

// 触发条件
if (ΔV ≥ 0 for consecutive_window steps) 
    throw StabilityViolationException;

if (cumulative_tokens ≥ budget_ceiling)
    throw BudgetExhaustedException;
```

#### GrowthRatioGuard算法
```csharp
// 热身期: 前warmup_turns步建立基线
baseline = median(tokens[0:warmup_turns])

// 监控期: 计算增长比率
for turn in turns[warmup_turns:]:
    ratio = tokens[turn] / baseline
    
    if (ratio > ratio_threshold && cumulative > budget_gate):
        consecutive_escalating++
    else:
        consecutive_escalating = 0
    
    if (consecutive_escalating >= window):
        throw StabilityViolationException;
```

### 2. RGDecimator文本压缩

#### 评分公式
```
relevance = w₁·keyword_density + w₂·info_density + w₃·(1 - redundancy)

其中:
  w₁ = 0.35 (关键词权重)
  w₂ = 0.30 (信息密度权重)
  w₃ = 0.35 (新颖性权重)
```

#### 评分维度
1. **关键词密度**: 结构性动词/错误指示器浓度
2. **信息密度**: 词汇多样性 × 长度归一化
3. **冗余惩罚**: 二元组Jaccard重叠度

#### 保留规则
- 第一条消息（系统提示）始终保留
- 最后一条消息（最新回合）始终保留
- 评分 ≥ threshold 的消息保留
- 最多保留 max_retained 条消息

### 3. HolographicEngine向量架构

#### 向量规格
- **维度**: D = 10,000（默认）
- **值域**: v ∈ {-1, 1}^D
- **存储**: int8（内存高效）

#### 核心运算
```
绑定(Bind):    a ⊗ b = elementwise_multiply(a, b)
捆绑(Bundle):   a + b = signum(elementwise_sum(a, b))
余弦相似度:    cos(a,b) = dot(a,b) / D
```

#### 不变性存储
```
H_inv = K_goal ⊗ V_safe  // 存储
V_safe = K_goal ⊗ H_inv  // 恢复
```

## 性能优化规范

### 1. 硬件加速策略

#### 检测层级
```csharp
enum HardwareAcceleration
{
    None = 0,      // 标量回退
    SSE2 = 1,      // x86基础
    SSE4 = 2,      // x86增强
    AVX = 3,       // 256位向量
    AVX2 = 4,      // 256位FMA
    AVX512 = 5,    // 512位向量
    NEON = 6,      // ARM基础
    AdvSIMD = 7    // ARM增强
}
```

#### 向量运算抽象层
```csharp
// 统一的硬件加速接口
public static class VectorOperations
{
    // 根据硬件选择最优实现
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static void MultiplyBipolar(
        Span<int> result,
        ReadOnlySpan<int> a,
        ReadOnlySpan<int> b)
    {
        if (Avx512F.IsSupported)
            MultiplyAvx512(result, a, b);
        else if (Avx2.IsSupported)
            MultiplyAvx2(result, a, b);
        else if (AdvSimd.IsSupported)
            MultiplyAdvSimd(result, a, b);
        else
            MultiplyScalar(result, a, b);
    }
    
    // AVX-512实现（Intel/AMD）
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe void MultiplyAvx512(
        Span<int> result,
        ReadOnlySpan<int> a,
        ReadOnlySpan<int> b)
    {
        fixed (int* pResult = result, pA = a, pB = b)
        {
            // 512位向量处理（16个int32）
            // 使用硬件内在函数
        }
    }
}
```

### 2. 内存管理规范

#### 内存池层次
```csharp
public static class MemoryPoolManager
{
    // 小对象池（< 1KB）
    private static readonly ArrayPoolbyte> SmallPool = 
        ArrayPool<byte>.Create(
            maxArrayLength: 1024,
            maxArraysPerBucket: 100);
    
    // 中对象池（1KB - 64KB）
    private static readonly ArrayPool<byte> MediumPool = 
        ArrayPool<byte>.Create(
            maxArrayLength: 64 * 1024,
            maxArraysPerBucket: 50);
    
    // 大向量池（VSA向量）
    private static readonly ConcurrentBag<IMemoryOwner<byte>> LargeVectorPool = 
        new ConcurrentBag<IMemoryOwner<byte>>();
    
    // 租借模式
    public static (IMemoryOwner<byte> Owner, Span<int> Span) RentVector(int length)
    {
        int byteLength = length * sizeof(int);
        
        if (byteLength <= 1024)
            return RentFromSmallPool(byteLength);
        else if (byteLength <= 64 * 1024)
            return RentFromMediumPool(byteLength);
        else
            return RentFromLargePool(byteLength);
    }
}
```

#### 零分配API设计
```csharp
public ref struct StabilityCheckResult
{
    public StabilityStatus Status;
    public double EnergyDelta;
    public int ConsecutiveWarnings;
    
    // 结构体避免堆分配
    public StabilityCheckResult(/* ... */) { }
}

public bool TryRecordStep(
    int tokensUsed,
    int errors,
    ref StabilityCheckResult result)
{
    // 使用ref结构体和Span避免分配
}
```

### 3. 线程安全实现

#### 锁策略层次
```csharp
public sealed class ThreadSafeLyapunovMonitor
{
    // 读写分离：读多写少的场景
    private readonly ReaderWriterLockSlim _stateLock = new(
        LockRecursionPolicy.NoRecursion);
    
    // 无锁计数器
    private struct AtomicMetrics
    {
        private long _tokens;
        private long _errors;
        private int _steps;
        
        public void Add(int tokens, int errors)
        {
            Interlocked.Add(ref _tokens, tokens);
            Interlocked.Add(ref _errors, errors);
            Interlocked.Increment(ref _steps);
        }
    }
    
    // 细粒度锁定
    private readonly object _energyHistoryLock = new();
    private readonly object _statusLock = new();
    
    public StabilityStatus RecordStep(int tokensUsed, int errors = 0)
    {
        // 快速路径：无锁更新计数器
        _atomicMetrics.Add(tokensUsed, errors);
        
        // 写入锁定：更新能量历史
        lock (_energyHistoryLock)
        {
            // 更新能量历史
        }
        
        // 读取锁定：检查状态
        _stateLock.EnterReadLock();
        try
        {
            return CheckStability();
        }
        finally
        {
            _stateLock.ExitReadLock();
        }
    }
}
```

#### 并发数据结构
```csharp
public class ConcurrentInvariantStore
{
    // 使用无锁字典存储不变性
    private readonly ConcurrentDictionary<string, HyperVector> _invariants;
    private readonly ConcurrentDictionary<string, HyperVector> _keys;
    
    // 使用MemoryBarrier保证可见性
    private volatile int _version;
    
    public void RegisterInvariant(string name, HyperVector key, HyperVector value)
    {
        var bound = VectorOperations.Bind(key.Span, value.Span);
        
        // 原子操作
        _invariants[name] = new HyperVector(bound);
        _keys[name] = key;
        
        // 内存屏障
        Interlocked.Increment(ref _version);
    }
}
```

## API设计规范

### 1. 主要公共接口

```csharp
// 核心监控接口
public interface IStabilityMonitor : IDisposable
{
    StabilityStatus RecordStep(int tokensUsed, int errors = 0);
    StabilityStatus CheckStability();
    void Reset();
    
    // 只读属性
    long TotalTokens { get; }
    int TotalSteps { get; }
    bool IsTripped { get; }
    bool IsFrozen { get; }
    StabilityConfig Config { get; }
}

// 增长比率监控
public interface IGrowthRatioGuard : IStabilityMonitor
{
    double? Baseline { get; }
    double? CurrentRatio { get; }
    int ConsecutiveEscalating { get; }
    void ResetEscalation();
}

// 文本压缩
public interface IRGDecimator
{
    ScoredMessage[] Decimate(ReadOnlySpan<string> messages);
    string[] Compress(ReadOnlySpan<string> messages);
    float ScoreMessage(ReadOnlySpan<char> message, ReadOnlySpan<string> context);
}

// 向量引擎
public interface IHolographicEngine : IDisposable
{
    int Dimensionality { get; }
    
    ReadOnlySpan<int> Bind(ReadOnlySpan<int> a, ReadOnlySpan<int> b);
    ReadOnlySpan<int> Bundle(ReadOnlySpan<ReadOnlySpan<int>> vectors);
    double CosineSimilarity(ReadOnlySpan<int> a, ReadOnlySpan<int> b);
    
    void RegisterInvariant(string name, ReadOnlySpan<int> key, ReadOnlySpan<int> value);
    bool TryRecover(string name, Span<int> output);
    double CheckDrift(string name, ReadOnlySpan<int> context);
}
```

### 2. 配置系统

```csharp
// Lyapunov配置
public sealed record StabilityConfig
{
    public int TokenBudget { get; init; } = 100_000;
    public double RatioThreshold { get; init; } = 2.0;
    public int Window { get; init; } = 3;
    public int WarmupTurns { get; init; } = 3;
    public int BudgetGate { get; init; } = 8_000;
    public double Lambda { get; init; } = 1.0;
    public bool Adaptive { get; init; } = false;
    public double AdaptiveK { get; init; } = 2.0;
    
    // 环境变量支持
    public static StabilityConfig FromEnvironment()
    {
        return new StabilityConfig
        {
            RatioThreshold = GetEnvDouble("STABILITY_RATIO_THRESHOLD", 2.0),
            BudgetGate = GetEnvInt("STABILITY_BUDGET_GATE", 8_000),
            Adaptive = GetEnvBool("STABILITY_ADAPTIVE", false)
        };
    }
}

// RG配置
public sealed record RGDecimatorConfig
{
    public float Threshold { get; init; } = 0.3f;
    public int MaxRetained { get; init; } = 50;
    public IReadOnlyList<string> StructuralKeywords { get; init; } = GetDefaultKeywords();
    
    // 权重配置
    public float KeywordWeight { get; init; } = 0.35f;
    public float InfoWeight { get; init; } = 0.30f;
    public float NoveltyWeight { get; init; } = 0.35f;
}

// VSA配置
public sealed record VSAConfig
{
    public int Dimensionality { get; init; } = 10_000;
    public int MaxInvariants { get; init; } = 20;
    public int WarnInvariants { get; init; } = 5;
    public bool UseNumpyOptimization { get; init; } = false;
}
```

### 3. 异常体系

```csharp
// 基础异常
public abstract class StabilityException : Exception
{
    protected StabilityException(string message) : base(message) { }
    protected StabilityException(string message, Exception inner) : base(message, inner) { }
}

// 具体异常类型
public sealed class StabilityViolationException : StabilityException
{
    public int ConsecutiveSteps { get; }
    public double PeakEnergy { get; }
    
    public StabilityViolationException(string message, int consecutiveSteps, double peakEnergy) 
        : base(message)
    {
        ConsecutiveSteps = consecutiveSteps;
        PeakEnergy = peakEnergy;
    }
}

public sealed class BudgetExhaustedException : StabilityException
{
    public long TokensUsed { get; }
    public long BudgetCeiling { get; }
    
    public BudgetExhaustedException(string message, long tokensUsed, long budgetCeiling)
        : base(message)
    {
        TokensUsed = tokensUsed;
        BudgetCeiling = budgetCeiling;
    }
}

public sealed class PermanentFailureException : StabilityException
{
    public string FailureReason { get; }
    public FailureReport Report { get; }
    
    public PermanentFailureException(string message, string reason, FailureReport report)
        : base(message)
    {
        FailureReason = reason;
        Report = report;
    }
}
```

### 4. 主要入口类

```csharp
public static class StabilityHarness
{
    // 全局配置
    public static StabilityConfig DefaultConfig { get; set; } = StabilityConfig.FromEnvironment();
    public static RGDecimatorConfig DefaultRGConfig { get; set; } = RGDecimatorConfig.Default;
    public static VSAConfig DefaultVSAConfig { get; set; } = VSAConfig.Default;
    
    // 工厂方法
    public static IGrowthRatioGuard CreateGuard(StabilityConfig? config = null)
    {
        return new GrowthRatioGuard(config ?? DefaultConfig);
    }
    
    public static IRGDecimator CreateDecimator(RGDecimatorConfig? config = null)
    {
        return new RGDecimator(config ?? DefaultRGConfig);
    }
    
    public static IHolographicEngine CreateHolographicEngine(VSAConfig? config = null)
    {
        return new HolographicEngine(config ?? DefaultVSAConfig);
    }
    
    // 批量操作
    public static IEnumerable<FailureReport> AnalyzeBatch(
        IEnumerable<IGrowthRatioGuard> guards,
        CancellationToken cancellationToken = default)
    {
        return guards.AsParallel()
                    .WithCancellation(cancellationToken)
                    .Select(g => FailureReport.FromGuard(g));
    }
    
    // 快速检查
    public static bool IsStable(IGrowthRatioGuard guard)
    {
        return guard.CheckStability() switch
        {
            StabilityStatus.Stable => true,
            StabilityStatus.Warning => true,  // 警告但未触发
            _ => false
        };
    }
    
    // 硬件信息
    public static HardwareInfo GetHardwareInfo()
    {
        return new HardwareInfo
        {
            Acceleration = HardwareAccelerator.Detect(),
            VectorSize = HardwareAccelerator.GetOptimalVectorSize(),
            Memory = GC.GetGCMemoryInfo()
        };
    }
}
```

## 诊断系统规范

### 1. 失败模式分类

```csharp
public enum FailurePattern
{
    // 失控模式
    ContextAccumulationSpiral,  // 上下文螺旋累积
    RetryStorm,                 // 重试风暴
    ToolLoop,                   // 工具调用循环
    PolicyDrift,                // 策略漂移
    
    // 资源模式
    BudgetExhaustion,           // 预算耗尽
    EarlyExplosion,             // 早期爆炸
    
    // 性能模式
    GradualDegradation,         // 逐渐退化
    
    // 成功模式
    HealthyCompletion,          // 健康完成
    
    // 未知
    Unknown                     // 未知模式
}
```

### 2. 诊断报告

```csharp
public sealed class FailureReport
{
    // 核心信息
    public FailurePattern Pattern { get; }
    public double Confidence { get; }          // 0.0-1.0
    public long TotalTokens { get; }
    public int TotalSteps { get; }
    public bool IsTripped { get; }
    public bool IsFrozen { get; }
    
    // 能量轨迹
    public ReadOnlyMemory<double> EnergyTrajectory { get; }
    public ReadOnlyMemory<double> DriftTrajectory { get; }
    
    // 比率信息
    public double? BaselineTokens { get; }
    public double? PeakRatio { get; }
    
    // 成本估算
    public double? CostEstimateUsd { get; }
    public double? ProjectedCostUsd { get; }
    
    // 建议和证据
    public IReadOnlyList<Suggestion> Suggestions { get; }
    public IReadOnlyList<string> Evidence { get; }
    
    // 工厂方法
    public static FailureReport FromGuard(IGrowthRatioGuard guard)
    {
        // 分析能量轨迹，分类模式
    }
    
    // 输出格式
    public string ToHumanReadable() { }
    public string ToJson(bool indent = true) { }
    public string ToCsvRow() { }
    public IReadOnlyDictionary<string, object> ToOtelAttributes() { }
}
```

### 3. 模式识别算法

```csharp
internal static class PatternClassifier
{
    public static (FailurePattern Pattern, double Confidence) Classify(
        ReadOnlySpan<double> energy,
        ReadOnlySpan<double> drift,
        bool isTripped,
        bool isFrozen)
    {
        if (!isTripped && !isFrozen)
            return (FailurePattern.HealthyCompletion, 0.95);
        
        if (isFrozen && !isTripped)
            return (FailurePattern.BudgetExhaustion, 0.90);
        
        // 早期爆炸检测（前3步）
        if (energy.Length >= 3 && energy[..3].Max() > 3.0)
            return (FailurePattern.EarlyExplosion, 0.85);
        
        // 上下文螺旋检测（最后5步）
        if (energy.Length >= 5)
        {
            var tail = energy[^5..];
            if (tail.All(v => v > 1.5))
            {
                int increasing = 0;
                for (int i = 1; i < tail.Length; i++)
                    if (tail[i] > tail[i-1]) increasing++;
                
                if (increasing >= 3)
                    return (FailurePattern.ContextAccumulationSpiral, 
                           Math.Min(0.6 + increasing * 0.08, 0.95));
            }
        }
        
        // 重试风暴检测（低方差）
        if (energy.Length >= 5)
        {
            double cv = CoefficientOfVariation(energy);
            if (cv < 0.3)
                return (FailurePattern.RetryStorm, 0.70);
        }
        
        // 策略漂移检测
        if (drift.Length >= 3)
        {
            double recent = drift[^3..].Average();
            double early = drift.Length >= 6 ? drift[..3].Average() : recent;
            
            if (recent > 0.7 && (recent - early) > 0.15)
                return (FailurePattern.PolicyDrift, 
                       Math.Min(0.5 + (recent - early), 0.9));
        }
        
        return (FailurePattern.Unknown, 0.3);
    }
}
```

## 测试规范

### 1. 单元测试要求
- **覆盖率**: ≥ 90% 代码覆盖率
- **性能测试**: 每个核心方法都有基准测试
- **并发测试**: 多线程场景下的正确性测试
- **边界测试**: 边界条件和异常路径测试

### 2. 基准测试规范
```csharp
[MemoryDiagnoser]
[ThreadingDiagnoser]
public class LyapunovBenchmarks
{
    private LyapunovMonitor _monitor;
    private const int Iterations = 1000000;
    
    [GlobalSetup]
    public void Setup()
    {
        _monitor = new LyapunovMonitor(lambda: 1.0, window: 3, budgetCeiling: 100_000);
    }
    
    [Benchmark]
    public void RecordStepBenchmark()
    {
        for (int i = 0; i < Iterations; i++)
        {
            _monitor.RecordStep(tokensUsed: 100 + i % 50, errors: i % 10);
        }
    }
    
    [Benchmark]
    public void ConcurrentRecordStepBenchmark()
    {
        Parallel.For(0, Iterations, i =>
        {
            _monitor.RecordStep(tokensUsed: 100 + i % 50, errors: i % 10);
        });
    }
}
```

### 3. 集成测试规范
- CLI工具集成测试
- Web API集成测试
- 与其他AI框架的兼容性测试

## 部署和发布规范

### 1. NuGet包配置
```xml
<!-- StabilityHarness.csproj -->
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <LangVersion>latest</LangVersion>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
    <EnableHardwareIntrinsics>true</EnableHardwareIntrinsics>
    
    <!-- NuGet元数据 -->
    <PackageId>StabilityHarness</PackageId>
    <Version>1.0.0</Version>
    <Authors>YourName</Authors>
    <Description>High-performance Lyapunov stability monitoring for AI agents</Description>
    <PackageTags>ai,llm,stability,monitoring,lyapunov,performance</PackageTags>
    <PackageLicenseExpression>MIT</PackageLicenseExpression>
  </PropertyGroup>
  
  <!-- 硬件内在函数支持 -->
  <PropertyGroup Condition="'$(RuntimeIdentifier)' == ''">
    <EnableAVX2>true</EnableAVX2>
    <EnableAVX512>true</EnableAVX512>
    <EnableAdvSIMD>true</EnableAdvSIMD>
  </PropertyGroup>
</Project>
```

### 2. 发布流程
1. **版本管理**: Semantic Versioning 2.0.0
2. **构建目标**: 
   - `net10.0` (主目标)
   - `netstandard2.1` (兼容版本)
3. **符号包**: 发布调试符号到NuGet
4. **源码包**: 可选的源码包发布

### 3. 文档要求
- XML文档注释（所有公共API）
- README.md（快速开始）
- API文档（使用DocFX或类似工具生成）
- 性能指南（最佳实践）

## 开发工作流程

### 1. 代码规范
- **命名**: PascalCase（类、方法），camelCase（局部变量）
- **注释**: 所有公共API必须有XML文档注释
- **格式**: 使用.editorconfig和dotnet format
- **安全性**: 使用SafeHandle管理非托管资源

### 2. 提交规范
- **提交信息**: Conventional Commits规范
- **分支策略**: Git Flow或GitHub Flow
- **代码审查**: 所有更改需要代码审查
- **CI/CD**: GitHub Actions或Azure DevOps

### 3. 质量门禁
- **静态分析**: Roslyn Analyzer启用所有规则
- **代码覆盖**: 新代码必须≥90%覆盖率
- **性能回归**: 禁止引入性能回归
- **API兼容性**: 保持向后兼容性

## 故障排除和调试

### 1. 日志系统
```csharp
public static class StabilityLogger
{
    // 结构化日志
    public static void LogStabilityEvent(
        StabilityStatus status,
        int step,
        long tokens,
        double energyDelta)
    {
        Logger.LogInformation(
            "Stability event: {Status} at step {Step}, tokens: {Tokens}, ΔV: {EnergyDelta:F2}",
            status, step, tokens, energyDelta);
    }
}
```

### 2. 性能分析点
- `LyapunovMonitor.RecordStep` - 核心热点
- `VectorOperations.MultiplyBipolar` - SIMD优化点
- `RGDecimator.ComputeScore` - 文本处理热点
- 内存分配分析（使用Memory Diagnostics）

### 3. 调试工具
- **性能分析器**: Visual Studio Profiler, dotnet-trace
- **内存分析**: dotnet-dump, PerfView
- **并发调试**: Parallel Stacks, Concurrency Visualizer

---

**版本**: 1.0.0  
**最后更新**: 2026-06-14  
**作者**: StabilityHarness团队  
**状态**: 草案（开发中）

---
*注意: 此规范文档会随着项目开发而更新。重大变更会记录在CHANGELOG.md中。*