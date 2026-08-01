#!/bin/bash

# ====================================
# NCF 桌面应用多平台发布脚本 (Unix/Linux/macOS)
# ====================================

set -e  # 遇到错误时停止

# 颜色定义
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
BLUE='\033[0;34m'
NC='\033[0m' # No Color

# 配置
PROJECT_NAME="NcfDesktopApp.GUI"
SOLUTION_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
OUTPUT_DIR="${SOLUTION_DIR}/publish"
BUILD_CONFIG="Release"

# 支持的平台
PLATFORMS=(
    "win-x64"
    "win-arm64"
    "osx-x64"
    "osx-arm64"
    "linux-x64"
    "linux-arm64"
)

# 获取平台显示名称的函数
get_platform_name() {
    case "$1" in
        "win-x64") echo "Windows x64" ;;
        "win-arm64") echo "Windows ARM64" ;;
        "osx-x64") echo "macOS Intel" ;;
        "osx-arm64") echo "macOS Apple Silicon" ;;
        "linux-x64") echo "Linux x64" ;;
        "linux-arm64") echo "Linux ARM64" ;;
        *) echo "$1" ;;
    esac
}

# 函数：显示帮助信息
show_help() {
    echo -e "${BLUE}用法: $0 [选项]${NC}"
    echo ""
    echo "选项:"
    echo "  -h, --help              显示此帮助信息"
    echo "  -c, --clean             发布前清理所有输出目录"
    echo "  -p, --platform PLATFORM 只发布指定平台 (可用: ${PLATFORMS[*]})"
    echo "  --self-contained        创建自包含发布 (包含.NET运行时)"
    echo "  --single-file           创建单文件发布"
    echo "  --no-restore            跳过包还原"
    echo ""
    echo "示例:"
    echo "  $0                      # 发布所有平台"
    echo "  $0 -c                   # 清理并发布所有平台"
    echo "  $0 -p win-x64           # 只发布Windows x64"
    echo "  $0 --self-contained     # 创建自包含版本"
}

# 函数：显示横幅
show_banner() {
    echo -e "${BLUE}"
    echo "======================================"
    echo "   NCF 桌面应用多平台发布工具"
    echo "======================================"
    echo -e "${NC}"
    echo "项目: $PROJECT_NAME"
    echo "解决方案目录: $SOLUTION_DIR"
    echo "输出目录: $OUTPUT_DIR"
    echo "构建配置: $BUILD_CONFIG"
    echo ""
}

# 函数：清理输出目录
clean_output() {
    echo -e "${YELLOW}🧹 清理输出目录...${NC}"
    if [ -d "$OUTPUT_DIR" ]; then
        rm -rf "$OUTPUT_DIR"
        echo -e "${GREEN}✅ 输出目录已清理${NC}"
    else
        echo -e "${YELLOW}⚠️  输出目录不存在，跳过清理${NC}"
    fi
    echo ""
}

# 函数：检查 .NET SDK
check_dotnet() {
    echo -e "${BLUE}🔍 检查 .NET SDK...${NC}"
    if ! command -v dotnet &> /dev/null; then
        echo -e "${RED}❌ 未找到 .NET SDK，请安装 .NET 10.0 或更高版本${NC}"
        exit 1
    fi
    
    DOTNET_VERSION=$(dotnet --version)
    echo -e "${GREEN}✅ .NET SDK 版本: $DOTNET_VERSION${NC}"
    echo ""
}

# 函数：还原包
restore_packages() {
    if [ "$NO_RESTORE" = true ]; then
        echo -e "${YELLOW}⏭️  跳过包还原${NC}"
        return
    fi
    
    echo -e "${BLUE}📦 还原 NuGet 包...${NC}"
    cd "$SOLUTION_DIR"
    if dotnet restore; then
        echo -e "${GREEN}✅ 包还原成功${NC}"
    else
        echo -e "${RED}❌ 包还原失败${NC}"
        exit 1
    fi
    echo ""
}

# 函数：发布平台
publish_platform() {
    local platform=$1
    local platform_name="${PLATFORM_NAMES[$platform]}"
    local platform_dir="$OUTPUT_DIR/$platform"
    
    echo -e "${BLUE}🚀 发布 $platform_name ($platform)...${NC}"
    
    # 构建发布命令
    local cmd="dotnet publish"
    cmd="$cmd -c $BUILD_CONFIG"
    cmd="$cmd -r $platform"
    cmd="$cmd -o \"$platform_dir\""
    
    if [ "$SELF_CONTAINED" = true ]; then
        cmd="$cmd --self-contained true"
    else
        cmd="$cmd --self-contained false"
    fi
    
    if [ "$SINGLE_FILE" = true ]; then
        cmd="$cmd -p:PublishSingleFile=true"
    fi
    
    # 执行发布
    cd "$SOLUTION_DIR"
    if eval $cmd; then
        # 检查发布结果
        if [ -d "$platform_dir" ] && [ "$(ls -A "$platform_dir")" ]; then
            local file_count=$(ls -1 "$platform_dir" | wc -l | tr -d ' ')
            echo -e "${GREEN}✅ $platform_name 发布成功 ($file_count 个文件)${NC}"
            
            # 显示主程序文件信息
            local main_exe=""
            if [[ $platform == win-* ]]; then
                main_exe="$platform_dir/${PROJECT_NAME}.exe"
            else
                main_exe="$platform_dir/${PROJECT_NAME}"
            fi
            
            if [ -f "$main_exe" ]; then
                local file_size=$(ls -lh "$main_exe" | awk '{print $5}')
                echo -e "${GREEN}   主程序: ${PROJECT_NAME}$([ "$platform" = "win-"* ] && echo ".exe") ($file_size)${NC}"
            fi
        else
            echo -e "${RED}❌ $platform_name 发布失败：输出目录为空${NC}"
            return 1
        fi
    else
        echo -e "${RED}❌ $platform_name 发布失败${NC}"
        return 1
    fi
    echo ""
}

# 函数：显示发布总结
show_summary() {
    echo -e "${BLUE}📊 发布总结${NC}"
    echo "======================================"
    
    if [ -d "$OUTPUT_DIR" ]; then
        for platform in "${PLATFORMS[@]}"; do
            local platform_dir="$OUTPUT_DIR/$platform"
            local platform_name=$(get_platform_name "$platform")
            
            if [ -d "$platform_dir" ] && [ "$(ls -A "$platform_dir")" ]; then
                local file_count=$(ls -1 "$platform_dir" | wc -l | tr -d ' ')
                local dir_size=$(du -sh "$platform_dir" 2>/dev/null | cut -f1)
                echo -e "${GREEN}✅ $platform_name: $file_count 个文件, $dir_size${NC}"
            else
                echo -e "${RED}❌ $platform_name: 发布失败${NC}"
            fi
        done
        
        echo ""
        echo -e "${BLUE}📁 发布位置: $OUTPUT_DIR${NC}"
        local total_size=$(du -sh "$OUTPUT_DIR" 2>/dev/null | cut -f1)
        echo -e "${BLUE}📦 总大小: $total_size${NC}"
    else
        echo -e "${RED}❌ 未找到发布输出${NC}"
    fi
    echo ""
}

# 解析命令行参数
CLEAN=false
SPECIFIC_PLATFORM=""
SELF_CONTAINED=false
SINGLE_FILE=false
NO_RESTORE=false

while [[ $# -gt 0 ]]; do
    case $1 in
        -h|--help)
            show_help
            exit 0
            ;;
        -c|--clean)
            CLEAN=true
            shift
            ;;
        -p|--platform)
            SPECIFIC_PLATFORM="$2"
            shift 2
            ;;
        --self-contained)
            SELF_CONTAINED=true
            shift
            ;;
        --single-file)
            SINGLE_FILE=true
            shift
            ;;
        --no-restore)
            NO_RESTORE=true
            shift
            ;;
        *)
            echo -e "${RED}未知选项: $1${NC}"
            show_help
            exit 1
            ;;
    esac
done

# 验证特定平台
if [ -n "$SPECIFIC_PLATFORM" ]; then
    if [[ ! " ${PLATFORMS[*]} " =~ " ${SPECIFIC_PLATFORM} " ]]; then
        echo -e "${RED}❌ 不支持的平台: $SPECIFIC_PLATFORM${NC}"
        echo -e "${YELLOW}支持的平台: ${PLATFORMS[*]}${NC}"
        exit 1
    fi
fi

# 主程序开始
START_TIME=$(date +%s)

show_banner
check_dotnet

if [ "$CLEAN" = true ]; then
    clean_output
fi

# 创建输出目录
mkdir -p "$OUTPUT_DIR"

restore_packages

# 发布平台
success_count=0
total_count=0

if [ -n "$SPECIFIC_PLATFORM" ]; then
    # 发布特定平台
    total_count=1
    if publish_platform "$SPECIFIC_PLATFORM"; then
        success_count=1
    fi
else
    # 发布所有平台
    total_count=${#PLATFORMS[@]}
    for platform in "${PLATFORMS[@]}"; do
        if publish_platform "$platform"; then
            ((success_count++))
        fi
    done
fi

# 显示总结
END_TIME=$(date +%s)
DURATION=$((END_TIME - START_TIME))

show_summary

echo -e "${BLUE}⏱️  总耗时: ${DURATION}秒${NC}"
echo -e "${BLUE}📈 成功率: $success_count/$total_count${NC}"

if [ $success_count -eq $total_count ]; then
    echo -e "${GREEN}🎉 所有平台发布成功！${NC}"
    exit 0
else
    echo -e "${YELLOW}⚠️  部分平台发布失败${NC}"
    exit 1
fi
