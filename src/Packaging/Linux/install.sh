#!/usr/bin/env bash
set -euo pipefail

source_dir=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
target_dir="${HOME}/.local/opt/ai-selection-toolbar"
apps_dir="${XDG_DATA_HOME:-${HOME}/.local/share}/applications"

if [[ ! -f "${source_dir}/AiSelectionToolbar.Linux" || ! -f "${source_dir}/AppIcon.svg" ]]; then
  echo '请在已解压的 Linux 发布包目录中运行 install.sh。' >&2
  exit 1
fi
mkdir -p -- "${target_dir}" "${apps_dir}"
if [[ "${source_dir}" != "${target_dir}" ]]; then
  cp -a -- "${source_dir}/." "${target_dir}/"
fi
chmod 755 -- "${target_dir}/AiSelectionToolbar.Linux"

cat > "${apps_dir}/ai-selection-toolbar.desktop" <<EOF
[Desktop Entry]
Type=Application
Name=AI 划词助手
Comment=选择文字后调用 AI 解释和翻译
Exec="${target_dir}/AiSelectionToolbar.Linux"
Icon=${target_dir}/AppIcon.svg
Terminal=false
Categories=Utility;Education;
EOF
chmod 644 -- "${apps_dir}/ai-selection-toolbar.desktop"
echo "已安装到 ${target_dir}。请从应用菜单启动 AI 划词助手。"
