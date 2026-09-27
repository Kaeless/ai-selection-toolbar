#!/usr/bin/env bash
set -euo pipefail
target_dir="${HOME}/.local/opt/ai-selection-toolbar"
desktop_file="${XDG_DATA_HOME:-${HOME}/.local/share}/applications/ai-selection-toolbar.desktop"
autostart_file="${XDG_CONFIG_HOME:-${HOME}/.config}/autostart/ai-selection-toolbar.desktop"
rm -f -- "${desktop_file}"
rm -f -- "${autostart_file}"
rm -rf -- "${target_dir}"
echo '程序已卸载；设置、历史与笔记保留在用户目录。'
