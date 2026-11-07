"""Root-only handoff of a local MCSManager connection; never prints credentials."""
import http.cookiejar
import json
import os
import pathlib
import secrets
import time
import urllib.error
import urllib.parse
import urllib.request

base = pathlib.Path('/opt/mcsmanager')
data = pathlib.Path('/var/lib/crafthome')
client = urllib.request.build_opener(urllib.request.ProxyHandler({}), urllib.request.HTTPCookieProcessor(http.cookiejar.CookieJar()))
key = None


def api(method, path, body=None, query=None):
    headers = {'X-Requested-With': 'XMLHttpRequest', 'Content-Type': 'application/json'}
    if key:
        headers['x-request-api-key'] = key
    url = 'http://127.0.0.1:23333' + path
    if query:
        url += '?' + urllib.parse.urlencode(query)
    request = urllib.request.Request(url, method=method, headers=headers,
                                     data=None if body is None else json.dumps(body).encode())
    with client.open(request, timeout=20) as response:
        result = json.load(response)
    if result.get('status') != 200:
        raise RuntimeError('Local engine request was rejected: ' + path)
    return result['data']


for attempt in range(60):
    try:
        client.open('http://127.0.0.1:23333/', timeout=2).close()
        break
    except (OSError, urllib.error.URLError):
        time.sleep(1)
else:
    raise SystemExit('MCSManager Web did not become ready.')

users = list((base / 'web/data/User').glob('*.json'))
private = pathlib.Path('/root/crafthome-mcsmanager-admin.json')


def enable_api_key():
    token = api('GET', '/api/auth/token')
    return api('PUT', '/api/auth/api', {'enable': True}, {'token': token})


if not users:
    username = 'CraftHomeOwner'
    # MCSManager accepts at most 36 characters and requires mixed case and digits.
    password = secrets.token_hex(16) + 'Aa1!'
    api('POST', '/api/auth/install', {'username': username, 'password': password})
    private.write_text(json.dumps({'username': username, 'password': password}, indent=2))
    private.chmod(0o600)
    key = enable_api_key()
else:
    for user_file in users:
        key = json.loads(user_file.read_text()).get('apiKey')
        if not key:
            continue
        try:
            api('GET', '/api/service/remote_services_list')
            break
        except (RuntimeError, urllib.error.HTTPError):
            key = None
    if not key and private.is_file():
        api('POST', '/api/auth/login', json.loads(private.read_text()))
        key = enable_api_key()
    if not key:
        raise SystemExit('Existing MCSManager has no usable administrator API key. Enable one in the existing panel; no account was reset.')

nodes = api('GET', '/api/service/remote_services_list')
node = next((item for item in nodes if item['ip'] in ('127.0.0.1', 'localhost', '::1') and item.get('available')), None)
if node is None:
    config = json.loads((base / 'daemon/data/Config/global.json').read_text())
    node_id = api('POST', '/api/service/remote_service', {
        'ip': '127.0.0.1', 'port': 24444, 'prefix': '', 'remarks': 'CraftHome local', 'apiKey': config['key']})
    api('GET', '/api/service/link_remote_service', query={'uuid': node_id})
    for attempt in range(30):
        node = next((item for item in api('GET', '/api/service/remote_services_list')
                     if item['uuid'] == node_id and item.get('available')), None)
        if node:
            break
        time.sleep(1)
    if node is None:
        raise SystemExit('Local daemon did not connect.')

handoff = data / 'engine-bootstrap.json'
handoff.write_text(json.dumps({'BaseUrl': 'http://127.0.0.1:23333', 'ApiKey': key, 'DaemonId': node['uuid']}))
handoff.chmod(0o600)
import pwd
owner = pwd.getpwnam('crafthome')
os.chown(handoff, owner.pw_uid, owner.pw_gid)
print('Local engine connection prepared; existing accounts and instances preserved.')
