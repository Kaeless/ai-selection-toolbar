#!/usr/bin/env bash
set -euo pipefail

project_dir=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd)
python_bin="${project_dir}/.venv/bin/python"
if [[ ! -x "${python_bin}" ]]; then
  python_bin="python3"
fi
version=$(sed -n 's/^__version__ = "\([^"]*\)"/\1/p' "${project_dir}/src/Linux/ai_selection_toolbar/__init__.py")
if [[ -z "${version}" ]]; then
  echo '无法读取 Linux 客户端版本号。' >&2
  exit 1
fi

dist_dir="${project_dir}/dist/linux-x64"
run_dir="${project_dir}/dist"
version_file="${run_dir}/.linux-package-version"
fixed_run="${run_dir}/AISelectionToolbar-linux-x64.run"
if [[ -f "${version_file}" && "$(<"${version_file}")" != "${version}" ]]; then
  output_run="${run_dir}/AISelectionToolbar-linux-x64-v${version}.run"
  auto_install=false
else
  output_run="${fixed_run}"
  auto_install=true
fi

cd -- "${project_dir}"
"${python_bin}" -m PyInstaller --noconfirm --clean --onefile --name AiSelectionToolbar.Linux \
  --paths src/Linux --collect-all keyring \
  --add-data 'src/Desktop/ManagementPage.html:web' \
  --add-data 'src/Desktop/ManagementPage.css:web' \
  --add-data 'src/Desktop/ManagementPage.js:web' \
  --add-data 'src/Desktop/Assets/AppIcon.svg:.' \
  src/Linux/launcher.py

mkdir -p -- "${dist_dir}"
mv -- dist/AiSelectionToolbar.Linux "${dist_dir}/AiSelectionToolbar.Linux"
cp -- src/Desktop/Assets/AppIcon.svg packaging/linux/install.sh packaging/linux/uninstall.sh "${dist_dir}/"
chmod 755 -- "${dist_dir}/AiSelectionToolbar.Linux" "${dist_dir}/install.sh" "${dist_dir}/uninstall.sh"

bash packaging/linux/make-installer.sh "${dist_dir}" "${output_run}"
printf '%s\n' "${version}" > "${version_file}"

if [[ "${auto_install}" == true ]]; then
  bash "${output_run}"
else
  echo "版本号已从旧版本变更为 ${version}，已生成新的安装包：${output_run}"
fi
