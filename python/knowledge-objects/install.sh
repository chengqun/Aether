#!/usr/bin/env bash
# 部署 knowledge-objects skill 到目标技能目录。
#
# 用法：
#   ./install.sh <目标目录>
#
# 说明：
# - 只部署 SKILL.md + scripts/（tests/README/install.sh 是开发物，不进部署）。
# - 拒绝 symlink 的宿主用本脚本拷贝；支持 symlink 的宿主可 ln -s 实现单一源。
set -euo pipefail

if [ $# -ne 1 ]; then
  echo "用法: $0 <目标技能目录>" >&2
  exit 1
fi

here="$(cd "$(dirname "$0")" && pwd)"
dest="$1"

mkdir -p "$dest/scripts"
cp "$here/SKILL.md" "$dest/SKILL.md"
cp "$here/scripts/ko.py" "$dest/scripts/ko.py"

echo "已部署到: $dest"
echo "内容: SKILL.md + scripts/ko.py（$(wc -l < "$here/scripts/ko.py" | tr -d ' ') 行）"
