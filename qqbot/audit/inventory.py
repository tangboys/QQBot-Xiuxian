"""仅解析源码 AST 的功能清单生成器，不导入或执行上游模块。"""
import ast
import hashlib
import json
import subprocess
from collections import Counter
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]

def describe(node):
    """保留常量值，动态表达式保留源码表达式供人工复核。"""
    try:
        value = ast.literal_eval(node)
        return sorted(value) if isinstance(value, set) else value
    except (ValueError, TypeError):
        return ast.unparse(node)

def main():
    """静态统计命令、定时器、Bot 调用与文件摘要并写出清单。"""
    files = []
    for path in sorted((ROOT / 'nonebot_plugin_xiuxian_2').rglob('*.py')):
        raw = path.read_bytes()
        tree = ast.parse(raw.decode('utf-8-sig'))
        entry = {'sourcepath': path.relative_to(ROOT).as_posix(), 'sha256': hashlib.sha256(raw).hexdigest(), 'commands': [], 'schedulerjobs': [], 'apis': [], 'imports': []}
        for node in ast.walk(tree):
            if isinstance(node, ast.Import):
                entry['imports'].extend(alias.name for alias in node.names)
            elif isinstance(node, ast.ImportFrom) and node.level == 0 and node.module:
                entry['imports'].append(node.module)
            if not isinstance(node, ast.Call):
                continue
            name = ast.unparse(node.func)
            if name in {'on_command', 'on_regex', 'on_fullmatch', 'on_message', 'on_keyword', 'on_startswith'}:
                entry['commands'].append({'line': node.lineno, 'matcher': name, 'command': describe(node.args[0]) if node.args else None, **{k.arg: describe(k.value) for k in node.keywords}})
            if name.endswith('.scheduled_job'):
                entry['schedulerjobs'].append({'line': node.lineno, 'args': [describe(a) for a in node.args], 'options': {k.arg: describe(k.value) for k in node.keywords}})
            if name.startswith('bot.') or name.startswith('get_bot().') or (name.startswith('get_bots()') and name.endswith('.call_api')):
                entry['apis'].append({'line': node.lineno, 'method': name, 'args': [describe(a) for a in node.args], 'keywords': [k.arg for k in node.keywords]})
        files.append(entry)
    result = {'upstream_commit': '128e31643b224350f1f0ce31f210553fd34334c1', 'verification': 'AST only; no upstream import, game execution, API, or tests', 'source_file_count': len(files), 'command_count': sum(len(f['commands']) for f in files), 'scheduler_job_count': sum(len(f['schedulerjobs']) for f in files), 'business_modules': [f['sourcepath'] for f in files if f['commands']], 'api_methods': sorted({a['method'] for f in files for a in f['apis']}), 'files': files}
    result['matcher_counts'] = dict(Counter(c['matcher'] for f in files for c in f['commands']))
    result['onebot_actions'] = ['get_group_list', 'send_group_msg', 'send_private_msg', 'send_group_forward_msg', 'send_private_forward_msg']
    result['onebot_action_note'] = '静态解析调用并人工追踪 boss api 常量；bot.send 为适配器便捷方法，不是 action。'
    assets = []
    for path in sorted((ROOT / 'data/xiuxian').rglob('*')):
        if path.is_file():
            raw = path.read_bytes()
            assets.append({'path': path.relative_to(ROOT).as_posix(), 'size': len(raw), 'sha256': hashlib.sha256(raw).hexdigest(), 'license': 'unknown; upstream asset provenance not independently verified'})
    (ROOT / 'qqbot/audit/asset-manifest.json').write_text(json.dumps(assets, ensure_ascii=False, indent=2), encoding='utf-8')
    baseline = json.loads((ROOT / 'qqbot/audit/upstream-manifest.json').read_text(encoding='utf-8'))
    before = {f['sourcepath']: f['sha256'] for f in baseline['files']}
    changes = [{'sourcepath': f['sourcepath'], 'before_sha256': before.get(f['sourcepath']), 'after_sha256': f['sha256']} for f in files if before.get(f['sourcepath']) != f['sha256']]
    (ROOT / 'qqbot/audit/source-changes.json').write_text(json.dumps(changes, ensure_ascii=False, indent=2), encoding='utf-8')
    diff = subprocess.run(['git', 'diff', '--', 'nonebot_plugin_xiuxian_2'], cwd=ROOT, check=True, capture_output=True).stdout
    (ROOT / 'qqbot/audit/upstream.patch').write_bytes(diff)
    (ROOT / 'qqbot/audit/feature-manifest.json').write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding='utf-8')
    print(json.dumps({k: v for k, v in result.items() if k != 'files'}, ensure_ascii=False))

if __name__ == '__main__':
    main()
