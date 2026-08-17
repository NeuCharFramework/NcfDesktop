#!/usr/bin/env bash
#----------------------------------------------------------------
# NCF Desktop Linux 启动器
# 用途：在特殊 Linux 环境（如 NVIDIA DGX Spark / Wayland / ARM64）
#       自动检测运行条件、补齐环境变量并启动自包含程序。
#
# 用法：
#   ./run-ncf-desktop.sh              # 自动检测并启动
#   ./run-ncf-desktop.sh --check      # 只检查，不启动
#   ./run-ncf-desktop.sh --software   # 强制软件渲染
#   ./run-ncf-desktop.sh --gpu        # 尝试 GPU 渲染（NVIDIA）
#   ./run-ncf-desktop.sh --help
#----------------------------------------------------------------

set -u

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SCRIPT_NAME="$(basename "${BASH_SOURCE[0]}")"

RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
BLUE='\033[0;34m'
CYAN='\033[0;36m'
NC='\033[0m'

MODE="auto"          # auto | software | gpu | check
EXTRA_APP_ARGS=()

print_help() {
    cat <<EOF
NCF Desktop Linux 启动器

用法:
  ./${SCRIPT_NAME} [选项] [-- 传给程序的参数...]

选项:
  --check, -c       只做环境检查，不启动
  --software, -s    强制 Avalonia 软件渲染（Spark / 透明窗口时优先）
  --gpu, -g         尝试 GPU 渲染（NVIDIA 建议配合 X11）
  --auto, -a        自动选择渲染策略（默认）
  --help, -h        显示帮助

示例:
  ./${SCRIPT_NAME}
  ./${SCRIPT_NAME} --software
  ./${SCRIPT_NAME} --check
EOF
}

while [[ $# -gt 0 ]]; do
    case "$1" in
        -h|--help)
            print_help
            exit 0
            ;;
        -c|--check)
            MODE="check"
            shift
            ;;
        -s|--software)
            MODE="software"
            shift
            ;;
        -g|--gpu)
            MODE="gpu"
            shift
            ;;
        -a|--auto)
            MODE="auto"
            shift
            ;;
        --)
            shift
            EXTRA_APP_ARGS+=("$@")
            break
            ;;
        -*)
            echo -e "${RED}未知选项: $1${NC}"
            print_help
            exit 1
            ;;
        *)
            EXTRA_APP_ARGS+=("$1")
            shift
            ;;
    esac
done

log_info()  { echo -e "${BLUE}ℹ️  $*${NC}"; }
log_ok()    { echo -e "${GREEN}✅ $*${NC}"; }
log_warn()  { echo -e "${YELLOW}⚠️  $*${NC}"; }
log_err()   { echo -e "${RED}❌ $*${NC}"; }
log_step()  { echo -e "${CYAN}▶ $*${NC}"; }

echo "========================================"
echo "🚀 NCF Desktop Linux Launcher"
echo "========================================"
echo ""

# ---------- 定位可执行文件 ----------
find_executable() {
    local candidates=(
        "${SCRIPT_DIR}/NcfDesktopApp.GUI-linux-arm64"
        "${SCRIPT_DIR}/NcfDesktopApp.GUI-linux-x64"
        "${SCRIPT_DIR}/../publish-self-contained/linux-arm64/NcfDesktopApp.GUI-linux-arm64"
        "${SCRIPT_DIR}/../publish-self-contained/linux-x64/NcfDesktopApp.GUI-linux-x64"
    )

    local host_arch
    host_arch="$(uname -m)"
    if [[ "$host_arch" == "aarch64" || "$host_arch" == "arm64" ]]; then
        candidates=(
            "${SCRIPT_DIR}/NcfDesktopApp.GUI-linux-arm64"
            "${SCRIPT_DIR}/../publish-self-contained/linux-arm64/NcfDesktopApp.GUI-linux-arm64"
            "${SCRIPT_DIR}/NcfDesktopApp.GUI-linux-x64"
            "${SCRIPT_DIR}/../publish-self-contained/linux-x64/NcfDesktopApp.GUI-linux-x64"
        )
    else
        candidates=(
            "${SCRIPT_DIR}/NcfDesktopApp.GUI-linux-x64"
            "${SCRIPT_DIR}/../publish-self-contained/linux-x64/NcfDesktopApp.GUI-linux-x64"
            "${SCRIPT_DIR}/NcfDesktopApp.GUI-linux-arm64"
            "${SCRIPT_DIR}/../publish-self-contained/linux-arm64/NcfDesktopApp.GUI-linux-arm64"
        )
    fi

    for path in "${candidates[@]}"; do
        if [[ -f "$path" ]]; then
            echo "$path"
            return 0
        fi
    done
    return 1
}

EXE_PATH="$(find_executable || true)"
if [[ -z "${EXE_PATH}" ]]; then
    log_err "未找到 NcfDesktopApp.GUI-linux-arm64 / linux-x64"
    log_info "请把本脚本放在 publish-self-contained/linux-* 目录旁，或与可执行文件同目录"
    exit 1
fi

chmod +x "$EXE_PATH" 2>/dev/null || true
log_ok "可执行文件: $EXE_PATH"

# ---------- 环境探测 ----------
HOST_ARCH="$(uname -m)"
OS_DESC="$(uname -srmo 2>/dev/null || uname -srm)"
SESSION_TYPE="${XDG_SESSION_TYPE:-unknown}"
HAS_WAYLAND=0
HAS_NVIDIA=0
IS_SPARK=0
HAS_DISPLAY=0
WEBKIT_OK=0
GTK_OK=0

[[ -n "${WAYLAND_DISPLAY:-}" || "$SESSION_TYPE" == "wayland" ]] && HAS_WAYLAND=1
[[ -n "${DISPLAY:-}" || -n "${WAYLAND_DISPLAY:-}" ]] && HAS_DISPLAY=1

if command -v nvidia-smi >/dev/null 2>&1 \
    || [[ -e /proc/driver/nvidia/version ]] \
    || ls /dev/nvidia* >/dev/null 2>&1; then
    HAS_NVIDIA=1
fi

# DGX Spark / GB10 粗检
if grep -qiE 'dgx.?spark|nvidia.?gb10|product.?name.*spark' /sys/class/dmi/id/* 2>/dev/null \
    || grep -qiE 'spark|gb10' /proc/device-tree/model 2>/dev/null \
    || [[ -f /etc/nvidia/dgx-release ]]; then
    IS_SPARK=1
fi
# 额外：ARM64 + NVIDIA 桌面栈也按“特殊环境”处理
if [[ $HAS_NVIDIA -eq 1 && ( "$HOST_ARCH" == "aarch64" || "$HOST_ARCH" == "arm64" ) ]]; then
    IS_SPARK=1
fi

check_lib() {
    local name="$1"
    if command -v ldconfig >/dev/null 2>&1 && ldconfig -p 2>/dev/null | grep -Fq "$name"; then
        return 0
    fi
    # 兜底：常见路径
    compgen -G "/usr/lib*/${name}*" >/dev/null && return 0
    compgen -G "/usr/lib/*/${name}*" >/dev/null && return 0
    return 1
}

if check_lib "libwebkit2gtk" || check_lib "libwebkit2gtk-4.1" || check_lib "libwebkit2gtk-4.0"; then
    WEBKIT_OK=1
fi
if check_lib "libgtk-3"; then
    GTK_OK=1
fi

EXE_ARCH="unknown"
if command -v file >/dev/null 2>&1; then
    case "$(file -b "$EXE_PATH")" in
        *aarch64*|*ARM\ aarch64*) EXE_ARCH="aarch64" ;;
        *x86-64*|*x86_64*) EXE_ARCH="x86_64" ;;
    esac
fi

echo ""
log_step "环境检查"
echo "  OS:            $OS_DESC"
echo "  Host arch:     $HOST_ARCH"
echo "  Binary arch:   $EXE_ARCH"
echo "  Session:       $SESSION_TYPE"
echo "  DISPLAY:       ${DISPLAY:-<empty>}"
echo "  WAYLAND:       ${WAYLAND_DISPLAY:-<empty>}"
echo "  NVIDIA GPU:    $([[ $HAS_NVIDIA -eq 1 ]] && echo yes || echo no)"
echo "  Spark-like:    $([[ $IS_SPARK -eq 1 ]] && echo yes || echo no)"
echo "  Graphical:     $([[ $HAS_DISPLAY -eq 1 ]] && echo yes || echo no)"
echo "  libgtk-3:      $([[ $GTK_OK -eq 1 ]] && echo found || echo missing)"
echo "  WebKitGTK:     $([[ $WEBKIT_OK -eq 1 ]] && echo found || echo missing)"
echo ""

WARNINGS=0

if [[ $HAS_DISPLAY -eq 0 ]]; then
    log_err "未检测到图形会话（DISPLAY / WAYLAND_DISPLAY 皆空）"
    log_info "请在桌面终端中运行，或先登录图形会话"
    WARNINGS=$((WARNINGS + 1))
fi

if [[ "$EXE_ARCH" != "unknown" ]]; then
    if [[ "$HOST_ARCH" == "aarch64" || "$HOST_ARCH" == "arm64" ]]; then
        if [[ "$EXE_ARCH" != "aarch64" ]]; then
            log_err "架构不匹配：主机是 ARM64，但程序是 $EXE_ARCH"
            exit 1
        fi
    elif [[ "$HOST_ARCH" == "x86_64" ]]; then
        if [[ "$EXE_ARCH" != "x86_64" ]]; then
            log_err "架构不匹配：主机是 x86_64，但程序是 $EXE_ARCH"
            exit 1
        fi
    fi
fi

if [[ $GTK_OK -eq 0 || $WEBKIT_OK -eq 0 ]]; then
    log_warn "缺少 WebView 相关库，内嵌浏览器可能空白"
    if command -v apt-get >/dev/null 2>&1; then
        echo "  建议安装:"
        echo "    sudo apt-get update"
        echo "    sudo apt-get install -y libgtk-3-0 libwebkit2gtk-4.1-0"
    elif command -v dnf >/dev/null 2>&1; then
        echo "  建议安装:"
        echo "    sudo dnf install -y gtk3 webkit2gtk4.1"
    fi
    WARNINGS=$((WARNINGS + 1))
fi

if [[ $HAS_WAYLAND -eq 1 ]]; then
    log_warn "当前是 Wayland 会话；Avalonia/WebView 在 NVIDIA 上容易出现透明/空白窗口"
    log_info "若仍异常，登录界面选择 Ubuntu on Xorg，或设置 WaylandEnable=false"
    WARNINGS=$((WARNINGS + 1))
fi

if [[ $MODE == "check" ]]; then
    if [[ $WARNINGS -eq 0 ]]; then
        log_ok "检查完成：未发现明显阻塞项"
        exit 0
    fi
    log_warn "检查完成：发现 $WARNINGS 个风险项（仍可尝试启动）"
    exit 0
fi

# ---------- 选择渲染策略 ----------
RENDER_MODE="$MODE"
if [[ "$MODE" == "auto" ]]; then
    if [[ $IS_SPARK -eq 1 || $HAS_WAYLAND -eq 1 || $HAS_NVIDIA -eq 1 ]]; then
        RENDER_MODE="software"
        log_info "自动策略: 特殊环境（Spark/NVIDIA/Wayland）→ 软件渲染"
    else
        RENDER_MODE="gpu"
        log_info "自动策略: 常规 Linux → 尝试 GPU 渲染"
    fi
fi

# 清理可能干扰的变量后再按策略设置
unset AVALONIA_RENDERING_MODE 2>/dev/null || true

case "$RENDER_MODE" in
    software)
        export AVALONIA_RENDERING_MODE=software
        export GDK_BACKEND=x11
        # NVIDIA + 软件路径下减少 GLX 干扰
        export LIBGL_ALWAYS_SOFTWARE="${LIBGL_ALWAYS_SOFTWARE:-1}"
        log_ok "渲染模式: software (AVALONIA_RENDERING_MODE=software, GDK_BACKEND=x11)"
        ;;
    gpu)
        export GDK_BACKEND=x11
        if [[ $HAS_NVIDIA -eq 1 ]]; then
            export __GLX_VENDOR_LIBRARY_NAME=nvidia
            log_ok "渲染模式: gpu (GDK_BACKEND=x11, NVIDIA GLX vendor)"
        else
            log_ok "渲染模式: gpu (GDK_BACKEND=x11)"
        fi
        ;;
    *)
        log_err "内部错误：未知渲染模式 $RENDER_MODE"
        exit 1
        ;;
esac

echo ""
log_step "启动环境变量"
echo "  AVALONIA_RENDERING_MODE=${AVALONIA_RENDERING_MODE:-<unset>}"
echo "  GDK_BACKEND=${GDK_BACKEND:-<unset>}"
echo "  LIBGL_ALWAYS_SOFTWARE=${LIBGL_ALWAYS_SOFTWARE:-<unset>}"
echo "  __GLX_VENDOR_LIBRARY_NAME=${__GLX_VENDOR_LIBRARY_NAME:-<unset>}"
echo ""

log_step "启动 NCF Desktop..."
echo "----------------------------------------"

cd "$(dirname "$EXE_PATH")"
exec "$EXE_PATH" "${EXTRA_APP_ARGS[@]}"
