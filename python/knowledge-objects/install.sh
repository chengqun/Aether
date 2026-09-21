#!/usr/bin/env bash
# 部署 knowledge-objects skill 到目标技能目录。
#
# 用法：
#   ./install.sh <目标目录>
#
# 示例：
#   ./install.sh ~/.deepbank/.claude/skills/knowledge-objects     # Claude Code（deepbank 配置）
#   ./install.sh ~/Library/Application\ Support/Aether/Desktop-Qifu/labs/skills/knowledge-objects  # Aether Qifu 租户
#
# 说明：
# - 只部署 SKILL.md + scripts/（tests/README/install.sh 是开发物，不进部署）。
# - Aether 侧必须用本脚本拷贝——MAF skill 路径硬化拒绝 symlink，软链的技能目录会被拒；
#   Claude Code 侧可改用软链（ln -s）指向本目录实现单一源。
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
