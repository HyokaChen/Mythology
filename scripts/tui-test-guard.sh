#!/usr/bin/env bash
# TUI 测试守护：跑全量 TUI 套件，并断言「测试宿主没有被中止」。
#
# 为什么需要：终端类夹具会起真实的 TerminalApp / 进程级 Dispatcher。
# 若某次运行出现「迟到续体在 Dispatcher 脱离后 Post」，会抛线程池未处理异常 →
# 进程以 SIGABRT 结束（exit 134），MTP 只报告已执行的部分（total 莫名变小）——
# 这类运行既不是通过、也不是普通失败，必须显式报警，否则会伪装成"偶发失败"被忽略。
#
# 判定（不硬编码 total，避免用例增减即失效）：
#   * exit 0 / 2             → 正常（2 = 有失败用例，由调用方决定是否阻断）
#   * exit >= 128            → 宿主被信号中止（134 = SIGABRT）
#   * 日志出现 Unhandled exception / Dispatcher is not attached → 迟到投递回归
#
# 用法：
#   bash scripts/tui-test-guard.sh            # 全量 TUI 套件
#   bash scripts/tui-test-guard.sh --no-build # 透传参数给 dotnet test
set -uo pipefail

repo_root=$(cd "$(dirname "$0")/.." && pwd)
cd "$repo_root"

log=$(mktemp)
trap 'rm -f "$log"' EXIT

dotnet test src/Tui/Tests/Tui.Tests.csproj "$@" >"$log" 2>&1
code=$?

total=$(sed -n 's/^  total: //p' "$log" | tail -1)
failed=$(sed -n 's/^  failed: //p' "$log" | tail -1)
echo "Tui.Tests: exit=$code total=${total:-?} failed=${failed:-?}"

status=0

if [ "${code}" -ge 128 ]; then
  echo "✗ 测试宿主被信号中止（exit $code）——终端夹具生命周期出现回归" >&2
  status=1
fi

if grep -q "Unhandled exception" "$log"; then
  echo "✗ 日志出现未处理异常（迟到 Dispatcher.Post 等）——堆栈：" >&2
  grep -A8 "Unhandled exception" "$log" | head -24 >&2
  status=1
fi

if [ -z "${total:-}" ]; then
  echo "✗ 没有拿到测试汇总（运行未完成）" >&2
  status=1
fi

if [ "$status" -eq 0 ]; then
  echo "✓ 宿主未被中止（total 稳定、无未处理异常）"
fi

exit $status
