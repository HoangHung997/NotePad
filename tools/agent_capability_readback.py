"""Independent readback of synthetic CAD/news/Office acceptance outputs."""
import argparse
from datetime import datetime, timezone
from email.utils import parsedate_to_datetime
import hashlib
import html
import json
from pathlib import Path
import urllib.request
import urllib.parse
import xml.etree.ElementTree as ET


def cad(root):
    checks = []
    for round in range(1, 4):
        work = root / f'cad-fixed-{round}'
        lines = (work/'sample.dxf').read_text(encoding='utf-8', errors='replace').splitlines()
        pairs = [(int(lines[i].strip()), lines[i+1].strip()) for i in range(0, len(lines)-1, 2)]
        start = next(i for i, pair in enumerate(pairs) if pair == (2, 'ENTITIES'))
        entities = []; current = None
        for code, value in pairs[start+1:]:
            if code == 0:
                if current: entities.append(current)
                if value == 'ENDSEC': break
                current = {0: value}
            elif current is not None: current[code] = value
        line = next(e for e in entities if e[0] == 'LINE')
        text = next(e for e in entities if e[0] == 'TEXT')
        checks.append(dict(round=round, passed=len(entities)==2 and float(line[10])==0 and float(line[11])==15
            and text[1]==f'CAD-EDITED-{round}' and float(text[40])==2.5
            and (work/'sample.dwg').read_bytes().startswith(b'AC10')
            and not (work/'disposable.dwg').exists() and not (work/'disposable.dxf').exists(),
            entities=entities, dwg_sha256=hashlib.sha256((work/'sample.dwg').read_bytes()).hexdigest()))
    return checks


def news(root, evidence=None, work_prefix='round'):
    sources = ['https://vnexpress.net/rss/tin-moi-nhat.rss', 'https://feeds.bbci.co.uk/news/world/rss.xml']
    observed = {}; checks = []
    def url(value):
        return urllib.parse.urlsplit(value.replace('\\u0026', '&'))._replace(query='', fragment='').geturl()
    for index, source in enumerate(sources):
        with urllib.request.urlopen(source, timeout=30) as response: data = response.read(4*1024*1024)
        (root/f'feed-observed-{index}.xml').write_bytes(data)
        for item in ET.fromstring(data).findall('.//item'):
            observed[url(item.findtext('link', ''))] = dict(title=html.unescape(item.findtext('title', '')).strip(),
                date=parsedate_to_datetime(item.findtext('pubDate', '')))
    for round in range(1, 4):
        work = root/f'{work_prefix}-{round}'
        round_observed = dict(observed)
        if evidence is not None:
            # Feeds are rolling windows; compare exact items actually fetched at execution
            # time as well as a separate fresh download, validating the recorded byte hash.
            task = json.loads((evidence/f'news-{round}/result.json').read_text(encoding='utf-8'))
            for item in task['done']['Evidence']:
                if not item['Summary'].startswith('web.read_feed observed:'): continue
                data = Path(item['LocalPath']).read_bytes()
                assert hashlib.sha256(data).hexdigest().lower() == item['Sha256'].lower()
                feed = json.loads(data)
                for article in feed['Items']:
                    round_observed[url(article['Url'])] = dict(title=article['Title'], date=datetime.fromisoformat(article['Published']))
        report = json.loads((work/'news.json').read_text(encoding='utf-8-sig'))
        articles = report['articles']; seen = set(); now = datetime.now(timezone.utc)
        records = []
        for article in articles:
            match = round_observed.get(url(article['url']))
            date = datetime.fromisoformat(article['published'].replace('Z', '+00:00'))
            title = article['title']; key = url(article['url'])
            valid = match is not None and html.unescape(title).strip() == match['title'] and date == match['date']
            valid = valid and 0 <= (now-date).total_seconds() <= 72*3600 and key not in seen and bool(article['summary_vi'])
            records.append(dict(title=title, url=key, passed=valid, source_title=None if match is None else match['title']))
            seen.add(key)
        checks.append(dict(round=round, passed=bool(articles) and len(articles)<=5 and all(r['passed'] for r in records), articles=records))
    return checks


if __name__ == '__main__':
    parser = argparse.ArgumentParser(); parser.add_argument('stage', choices=['cad', 'news']); parser.add_argument('root', type=Path)
    parser.add_argument('--evidence', type=Path)
    parser.add_argument('--work-prefix', default='round')
    args = parser.parse_args(); checks = news(args.root.resolve(), args.evidence, args.work_prefix) if args.stage == 'news' else cad(args.root.resolve())
    result = dict(stage=args.stage, checks=checks, passed=all(c['passed'] for c in checks))
    (args.root/f'verify-{args.stage}.json').write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding='utf-8')
    print(json.dumps(result, ensure_ascii=False)); raise SystemExit(0 if result['passed'] else 1)
