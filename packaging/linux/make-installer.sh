#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 2 ]]; then
  echo "用法：$0 <发布目录> <输出.run>" >&2
  exit 2
fi

source_dir=$(cd -- "$1" && pwd)
output_file=$2
mkdir -p -- "$(dirname -- "$output_file")"

cat > "$output_file" <<'INSTALLER'
#!/usr/bin/env bash
set -euo pipefail

payload_line=$(awk '/^__AI_TOOLBAR_PAYLOAD__$/ {print NR + 1; exit}' "$0")
if [[ -z "$payload_line" ]]; then
  echo '安装包内容损坏，找不到数据段。' >&2
  exit 1
fi
temporary_dir=$(mktemp -d)
trap 'rm -rf -- "$temporary_dir"' EXIT
tail -n +"$payload_line" "$0" | tar -xzf - -C "$temporary_dir"
bash "$temporary_dir/install.sh"
echo 'AI 划词助手安装完成。'
exit 0

__AI_TOOLBAR_PAYLOAD__
INSTALLER

tar -C "$source_dir" -czf - . >> "$output_file"
chmod 755 -- "$output_file"
echo "已生成：$output_file"
