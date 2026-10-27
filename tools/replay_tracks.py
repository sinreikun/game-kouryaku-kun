# v0.31.6 診断用: 構造化観測に残った候補（枠・中心・区画数・見た目のハッシュ）だけで、旧と新の対応付けを再生して分断を比べる。
# 色の分布（Hist）と変化した区画の集合は残っていないので、Hist は使わず、「前の区画が変わった」は前の手の候補だったか・枠が今の候補と重なるかで近似する。
import json, math, sqlite3, sys, statistics as st, collections

def load(db, rid):
    c = sqlite3.connect(db); steps = []
    for s, j in c.execute('select step,json from structured_observations where run_id=? order by seq', (rid,)):
        e = json.loads(j).get('entity_layer')
        if not isinstance(e, dict): steps.append(None); continue
        steps.append(([dict(x0=k['x0'], y0=k['y0'], x1=k['x1'], y1=k['y1'], cx=k['cx'], cy=k['cy'], cells=k['cells'], app=k['appearance']) for k in e['candidates']], e.get('common_shift')))
    return steps

def ov(a, b, dx=0, dy=0):
    return min(a['x1'] + dx, b['x1']) >= max(a['x0'] + dx, b['x0']) and min(a['y1'] + dy, b['y1']) >= max(a['y0'] + dy, b['y0'])

class T:
    def __init__(s, i, o, step): s.id = i; s.missing = False; s.vx = s.vy = 0; s.last = step; s.states = set(); s.seen = 0; s.born = step; s.upd(o, step, True)
    def upd(s, o, step, new=False):
        if not new:
            g = max(1, step - s.last); s.vx = (o['cx'] - s.cx) / g; s.vy = (o['cy'] - s.cy) / g
        for k in ('x0', 'y0', 'x1', 'y1', 'cx', 'cy', 'cells', 'app'): setattr(s, k, o[k])
        s.states.add(o['app']); s.last = step; s.seen += 1; s.missing = False
    def box(s): return dict(x0=s.x0, y0=s.y0, x1=s.x1, y1=s.y1)

EDGES = [[.5, 1, 2, 3, 5, 8, 13], [.2, .5, 1, 2], [.2, .5, 1, 2], [.5], [.5], [.5], [.5, 1.5, 3.5, 7.5]]
def bins(f): return [sum(1 for e in E if v >= e) for v, E in zip(f, EDGES)]
class NB:
    def __init__(s): s.S = [[1.0] * (len(e) + 1) for e in EDGES]; s.D = [[1.0] * (len(e) + 1) for e in EDGES]; s.sn = 1; s.dn = 1
    def post(s, f): return sum(math.log(s.S[i][b] / sum(s.S[i])) - math.log(s.D[i][b] / sum(s.D[i])) for i, b in enumerate(bins(f))) + math.log(s.sn / s.dn)
    def learn(s, f, same):
        t = s.S if same else s.D
        for i, b in enumerate(bins(f)): t[i][b] += 1
        if same: s.sn += 1
        else: s.dn += 1

DBG = None
VEL = False
DALL = True
def R_ov(u, o): return ov(u.box(), o)
def run(steps, new):
    tracks = []; nid = [1]; out = []; nb = NB(); reid = 0; created = 0
    prevc = set()
    for step, x in enumerate(steps):
        if x is None: out.append({}); continue
        obs, sh = x
        for t in tracks: t.scr = (t.cx, t.cy, t.x0, t.y0, t.x1, t.y1)
        if sh:
            for t in tracks:
                if not t.missing: t.cx += sh[0]; t.cy += sh[1]; ix, iy = round(sh[0]), round(sh[1]); t.x0 += ix; t.x1 += ix; t.y0 += iy; t.y1 += iy
        live = [t for t in tracks if not t.missing and (t.id in prevc or any(ov(t.box(), o) for o in obs))]
        liveids = {t.id for t in live}
        assign = {}
        taken = set()
        def F(t, o):
            gap = max(0, step - t.last - 1) if t.missing else 0
            hy = [(0.0, 0.0)]
            if new and not t.missing:
                if sh: hy.append((-sh[0], -sh[1]))   # 画面に固定（背景と一緒にずれない）
                if VEL and (t.vx or t.vy): hy.append((t.vx, t.vy))
            best = None
            for vx, vy in hy:
                ow, oh = o['x1'] - o['x0'] + 1, o['y1'] - o['y0'] + 1; tw, th = t.x1 - t.x0 + 1, t.y1 - t.y0 + 1
                f = [math.hypot(o['cx'] - t.cx - vx, o['cy'] - t.cy - vy), abs(math.log(max(1, o['cells']) / max(1, t.cells))), abs(math.log(ow / oh) - math.log(tw / th)),
                     1 if o['app'] in t.states else 0, 1 if ov(t.box(), o, round(vx), round(vy)) else 0, 1 if t.id in liveids else 0, gap]
                if best is None or nb.post(f) > nb.post(best) or (nb.post(f) == nb.post(best) and f[0] < best[0]): best = f
            return best
        if new:
            # 割れた塊をまとめる
            par = list(range(len(obs)))
            def fd(a):
                while par[a] != a: a = par[a]
                return a
            for t in live:
                ix, iy = round(t.vx), round(t.vy)
                mine = [i for i, o in enumerate(obs) if ov(t.box(), o, ix, iy) and sum(1 for u in live if ov(u.box(), o, round(u.vx), round(u.vy))) == 1
                        and o['x0'] >= t.x0 + ix and o['x1'] <= t.x1 + ix and o['y0'] >= t.y0 + iy and o['y1'] <= t.y1 + iy]
                for a in mine:
                    for b in mine:
                        if a < b and obs[a]['x0'] - 1 <= obs[b]['x1'] and obs[b]['x0'] - 1 <= obs[a]['x1'] and obs[a]['y0'] - 1 <= obs[b]['y1'] and obs[b]['y0'] - 1 <= obs[a]['y1']: par[fd(b)] = fd(a)
            g = collections.defaultdict(list)
            for i in range(len(obs)): g[fd(i)].append(obs[i])
            obs = [v[0] if len(v) == 1 else dict(x0=min(o['x0'] for o in v), y0=min(o['y0'] for o in v), x1=max(o['x1'] for o in v), y1=max(o['y1'] for o in v),
                    cx=sum(o['cx'] * o['cells'] for o in v) / sum(o['cells'] for o in v), cy=sum(o['cy'] * o['cells'] for o in v) / sum(o['cells'] for o in v),
                    cells=sum(o['cells'] for o in v), app=max(v, key=lambda o: o['cells'])['app']) for v in g.values()]
            feats = {(t.id, i): F(t, o) for i, o in enumerate(obs) for t in tracks}
            for i, o in enumerate(obs):
                ts = [t for t in tracks if not t.missing and feats[(t.id, i)][4]]
                if len(ts) == 1 and ts[0].id in liveids and sum(1 for j in range(len(obs)) if feats[(ts[0].id, j)][4]) == 1: assign[i] = (ts[0], True); taken.add(ts[0].id)
            pairs = [(nb.post(feats[(t.id, i)]), i, t) for i in range(len(obs)) if i not in assign for t in tracks if t.id not in taken]
            pairs.sort(key=lambda p: (-p[0], p[1], p[2].id))
            for sc, i, t in pairs:
                if sc <= 0: break
                if i in assign or t.id in taken: continue
                sec = max([p[0] for p in pairs if (p[1] == i) != (p[2].id == t.id)] or [-1e9])
                assign[i] = (t, sc - sec >= math.log(10)); taken.add(t.id)
        cost = lambda t, o: math.hypot(o['cx'] - t.cx - (t.vx if new else 0), o['cy'] - t.cy - (t.vy if new else 0))
        rest = [i for i in range(len(obs)) if i not in assign]
        for i in rest:
            cand = [t for t in live if t.id not in taken]
            if not cand: break
            best = min(cand, key=lambda t: (cost(t, obs[i]), t.id))
            back = min([j for j in rest if j not in assign], key=lambda j: (cost(best, obs[j]), j))
            if back != i: continue
            assign[i] = (best, False); taken.add(best.id)
        if new:
            # d) 残った候補: 前の枠（速度込み）と重なる、前の区画が変わった追跡のうち事後確率のいちばん高いもの
            for i in range(len(obs)):
                if i in assign: continue
                c2 = [t for t in (tracks if DALL else live) if t.id not in taken and feats[(t.id, i)][4]]
                if c2:
                    t = max(c2, key=lambda t: (nb.post(feats[(t.id, i)]), -t.id)); assign[i] = (t, False); taken.add(t.id)
            for i, (t, sure) in assign.items():
                if sure:
                    nb.learn(feats[(t.id, i)], True)
                    for u in tracks:
                        if u.id != t.id: nb.learn(feats[(u.id, i)], False)
        ids = {}
        used = set()
        for i, o in enumerate(obs):
            if i in assign:
                t = assign[i][0]
                if t.missing: reid += 1
            elif not new and (re := next((t for t in tracks if t.missing and t.app == o['app'] and t.id not in used), None)): t = re; reid += 1
            elif not new and (pt := next((t for t in sorted(tracks, key=lambda t: (-(o['app'] in t.states), t.id)) if t.id not in used and ov(t.box(), o)), None)): t = pt
            else:
                if new and DBG is not None:
                    bp = max([nb.post(feats[(u.id, i)]) for u in tracks if u.id not in used] or [-99])
                    why = 'app' if any(u.missing and u.app == o['app'] for u in tracks) else 'ovl' if any(u.id not in used and R_ov(u, o) for u in tracks) else 'none'
                    DBG[(why, 'tiny' if o['cells'] <= 2 else 'big', 'pos' if bp > 0 else 'neg')] += 1
                t = T(nid[0], o, step); nid[0] += 1; tracks.append(t); created += 1; used.add(t.id); ids[i] = (t.id, o); continue
            t.upd(o, step); used.add(t.id); ids[i] = (t.id, o)
        for t in live:
            if t.id not in used: t.missing = True
        while len(tracks) > 256:
            m = next((t for t in tracks if t.missing), None)
            if not m: break
            tracks.remove(m)
        prevc = set(used)
        out.append(ids)
    life = [t.seen for t in tracks]
    return out, created, reid, life

def metrics(out, created, reid, life, steps):
    seq = []
    for ids in out:
        cs = [(tid, o) for tid, o in ids.values() if 11 <= o['cx'] <= 20 and 8 <= o['cy'] <= 16 and o['cells'] >= 6]
        seq.append(max(cs, key=lambda z: z[1]['cells']) if cs else None)
    near = [(a, b) for a, b in zip(seq, seq[1:]) if a and b and abs(a[1]['cx'] - b[1]['cx']) + abs(a[1]['cy'] - b[1]['cy']) <= 3]
    nsw = sum(a[0] != b[0] for a, b in near)
    seq = [x[0] for x in seq if x]
    n = sum(1 for x in steps if x)
    return dict(center_steps=len(seq), center_ids=len(set(seq)), switches=sum(a != b for a, b in zip(seq, seq[1:])), near_pairs=len(near), near_switches=nsw, new_per_step=round(created / max(1, n), 2), reid=reid,
                life_mean=round(st.mean(life), 2) if life else 0, life_median=st.median(life) if life else 0)

if __name__ == '__main__':
    db, rid = sys.argv[1], int(sys.argv[2]); steps = load(db, rid)
    # 記録された番号そのもの（実機の旧の結果）
    rec = []
    c = sqlite3.connect(db)
    for s, j in c.execute('select step,json from structured_observations where run_id=? order by seq', (rid,)):
        e = json.loads(j).get('entity_layer'); rec.append({i: (k['track'], dict(cx=k['cx'], cy=k['cy'], cells=k['cells'])) for i, k in enumerate(e['candidates'])} if isinstance(e, dict) else {})
    ntr = len({t for r in rec for t, _ in r.values()})
    seen = collections.Counter(t for r in rec for t, _ in r.values())
    print('recorded', metrics(rec, ntr, 0, list(seen.values()), steps))
    for name, new in (('old-replay', False), ('new-replay', True)):
        o, cr, re, life = run(steps, new); print(name, metrics(o, cr, re, life, steps))
