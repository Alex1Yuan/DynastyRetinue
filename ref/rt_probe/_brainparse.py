"""Parse BlueprintBrain records out of blueprints-pack.bbp.

Layout discovered by hexdump of two calibration records:
  [0x00] 16 bytes  TypeId guid  (d0e20ba43f1689d4a964ffe958a5fa1a == BlueprintBrain)
  [0x10]  9 bytes  unknown (zero in samples)
  [0x19]  int32    unknown (107 / -1)
  [0x1d]  string   .NET 7-bit-len prefixed comment (may be empty)
           ---- BlueprintBrain fields, in C# declaration order ----
           int32   ScoreOrder.order.Length (9 legacy or 10 current)
           N * (int32 ScoreType, int32 ScoreFactor)
           int32   HatedTargetConditions.Length
           ... N PropertyCalculator (only parsed when N==0)
           byte    m_TargetOthersIfCantReachHated
           byte    m_UseOnlyListedAbilities
           int32   AbilityPriorityOrder.order.Length
           N * AbilitySourceWrapper
           int32   MovementInfluentAbilities.Length
           N * AbilitySourceWrapper

  AbilitySourceWrapper = int32 Type | str m_Ability | str m_Equipment
                         | int32 randomGroupCount | count * str
  (str = 7-bit-len prefixed; length 0 == null reference)
"""
import sys, struct, json, io, collections
sys.path.insert(0, r'D:\RT_RetinueMod\ref\rt_probe')
import bbp

BRAIN_TYPEID = 'd0e20ba43f1689d4a964ffe958a5fa1a'
SCORE_NAMES = ['EnemyCover', 'EffectiveDistance', 'EnemyThreat', 'Priority',
               'Threats', 'Hide', 'StayingAway', 'EnemyHPLeft', 'Closiness',
               'BodyGuard']
FACTOR_NAMES = ['Default', 'Inverted', 'Ignored']
SRC_TYPE = ['Ability', 'Equipment', 'RandomGroup']


class R:
    def __init__(self, b):
        self.b = b
        self.p = 0

    def u8(self):
        v = self.b[self.p]; self.p += 1; return v

    def i32(self):
        v = struct.unpack_from('<i', self.b, self.p)[0]; self.p += 4; return v

    def s(self):
        """.NET BinaryWriter string: 7-bit encoded length + utf8 bytes."""
        n = 0; sh = 0
        while True:
            c = self.b[self.p]; self.p += 1
            n |= (c & 0x7f) << sh
            if not (c & 0x80):
                break
            sh += 7
            if sh > 35:
                raise ValueError('bad 7bit len')
        raw = self.b[self.p:self.p + n]; self.p += n
        return raw.decode('utf-8', 'replace')


def read_src_wrapper(r):
    t = r.i32()
    if t not in (0, 1, 2):
        raise ValueError('bad AbilitySourceType %d' % t)
    ab = r.s()
    eq = r.s()
    n = r.i32()
    if not (0 <= n <= 64):
        raise ValueError('bad randomGroup count %d' % n)
    grp = [r.s() for _ in range(n)]
    return {'type': SRC_TYPE[t], 'ability': ab, 'equipment': eq, 'group': grp}


def parse(blob):
    """Returns dict of the fields we care about, or raises."""
    r = R(blob)
    tid = bbp.guid_net(blob[:16]); r.p = 16
    if tid != BRAIN_TYPEID:
        raise ValueError('not BlueprintBrain typeid=%s' % tid)
    proto = r.s()                 # prototype/override parent blueprint guid ('' = none)
    nov = r.i32()                 # number of overridden field names
    if not (0 <= nov <= 64):
        raise ValueError('bad override count %d' % nov)
    overrides = [r.s() for _ in range(nov)]
    if proto:
        # partial "patch" blueprint: only overridden fields are present
        return {'proto': proto, 'overrides': overrides, 'protopatch': True,
                'partial': True, 'score_order': [], 'hated': -1}
    r.i32()                       # unknown int (0 in all samples)
    r.i32()                       # unknown int (asset revision?)
    comment = r.s()

    n = r.i32()
    if not (8 <= n <= 10):
        raise ValueError('bad ScoreOrder count %d' % n)
    order = []
    seen = set()
    for _ in range(n):
        t = r.i32(); fa = r.i32()
        if not (0 <= t < 10) or not (0 <= fa < 3) or t in seen:
            raise ValueError('bad ScorePair %d/%d' % (t, fa))
        seen.add(t)
        order.append((SCORE_NAMES[t], FACTOR_NAMES[fa]))

    hated = r.i32()
    if hated != 0:
        # PropertyCalculator is polymorphic/variable; bail out rather than guess
        return {'comment': comment, 'score_order': order, 'hated': hated,
                'partial': True, 'protopatch': False, 'hated_at': r.p}
    target_others = r.u8()
    use_only = r.u8()
    if target_others > 1 or use_only > 1:
        raise ValueError('bad bools %d/%d' % (target_others, use_only))
    n = r.i32()
    if not (0 <= n <= 64):
        raise ValueError('bad AbilityPriorityOrder count %d' % n)
    prio = [read_src_wrapper(r) for _ in range(n)]
    n = r.i32()
    if not (0 <= n <= 64):
        raise ValueError('bad MovementInfluent count %d' % n)
    movi = [read_src_wrapper(r) for _ in range(n)]
    out = {'comment': comment, 'score_order': order, 'hated': hated,
           'target_others': bool(target_others),
           'use_only_listed': bool(use_only),
           'priority': prio, 'movement': movi, 'partial': False,
           'protopatch': False, 'end': r.p}

    # ---- tail. Only fully decodable when AbilitySettings is empty
    # (AbilitySettings embeds polymorphic PropertyCalculator[]).
    try:
        nset = r.i32()
        out['n_ability_settings'] = nset
        if nset != 0:
            out['tail'] = False
            return out
        out['hit_unintended_penalty'] = struct.unpack_from('<f', r.b, r.p)[0]
        r.p += 4
        out['is_careful_shooter'] = bool(r.u8())
        out['ignore_aoo_for_casting'] = bool(r.u8())
        out['response_to_aoo_threat'] = bool(r.u8())
        out['response_to_aoo_threat_after'] = bool(r.u8())
        out['melee_brain_type'] = r.i32()
        out['n_before_move'] = r.i32()
        out['n_move_and_cast'] = r.i32()
        out['n_after_move'] = r.i32()
        # self-check: we must now be sitting exactly on <len><name><0x20><guid>
        nm2 = r.s()
        gd = r.s()
        out['tail'] = (r.p == len(r.b) and len(gd) == 32
                       and all(c in '0123456789abcdef' for c in gd))
        out['tail_name'] = nm2
        out['tail_guid'] = gd
    except Exception as ex:
        out['tail'] = False
        out['tail_err'] = str(ex)[:60]
    return out


def load_names():
    return json.load(io.open(r'D:\RT_RetinueMod\ref\rt_probe\cp\g2n.json',
                             encoding='utf-8'))


def tail_name(blob):
    """The record ends with <7bit-len name><0x20 assetguid>. Recover the name."""
    g = blob[-32:].decode('ascii', 'replace')
    # walk back: 0x20 length byte then name
    if blob[-33] != 0x20:
        return None, None
    i = len(blob) - 33
    # name length is 7-bit encoded right before
    for back in (1, 2):
        st = i - back
        try:
            rr = R(blob[st:]); nm = rr.s()
            if rr.p == back + len(nm.encode('utf-8')) and nm and nm.isprintable():
                return nm, g
        except Exception:
            pass
    return None, g
