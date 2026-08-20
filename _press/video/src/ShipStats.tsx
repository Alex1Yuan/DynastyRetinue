import {
  AbsoluteFill,
  Easing,
  Interactive,
  interpolate,
  Sequence,
  useCurrentFrame,
  useVideoConfig,
} from "remotion";
import { BLUE, FONT, GOLD, INK, MONO, MUTED, TEXT } from "./theme";

/**
 * 座舰加成对照 —— 8 秒，全片高潮那一段的叠加图。
 *
 * ★数字全部取自代码里的默认值★（Main.cs 的 Settings 字段），不是从某次日志里抄的：
 *   ShipCruiserShieldPct=50 / ShipGrandShieldPct=100
 *   ShipCruiserArmourPct=50 / ShipGrandArmourPct=100
 *   ShipCruiserRamPct=100   / ShipGrandRamPct=200
 *   ShipCruiserBroadside=1  / ShipGrandBroadside=2
 *   ShipCruiserRange=3      / ShipGrandRangeProw=5
 * 面板上这些都是滑条，所以文案里说的是"默认值"。
 *
 * 用百分比和增量、不用绝对数值：护盾上限的基数随船而变，
 * 写死「80 → 160」会有玩家对不上，写 +100% 永远成立。
 */

type Row = {
  label: string;
  cruiser: string;
  grand: string;
  /** 大巡那一列做数字滚动时的终值；为空则不滚动，直接显示 */
  grandNum?: number;
  grandSuffix?: string;
  grandPrefix?: string;
};

const ROWS: Row[] = [
  { label: "护盾上限", cruiser: "+50%", grand: "", grandPrefix: "+", grandNum: 100, grandSuffix: "%" },
  { label: "装甲减伤", cruiser: "+50%", grand: "", grandPrefix: "+", grandNum: 100, grandSuffix: "%" },
  { label: "撞角行程", cruiser: "速度 ×1", grand: "", grandPrefix: "速度 ×", grandNum: 2 },
  { label: "舷炮开火次数", cruiser: "+1", grand: "", grandPrefix: "+", grandNum: 2 },
  { label: "舰首射程", cruiser: "+3", grand: "", grandPrefix: "+", grandNum: 5 },
];

const HEAD = 0.9; // 表头出现耗时（秒）
const STEP = 0.42; // 每行错开
const ROLL = 0.75; // 数字滚动时长

const StatRow: React.FC<{ row: Row; index: number }> = ({ row, index }) => {
  const frame = useCurrentFrame();
  const { fps } = useVideoConfig();
  const t0 = (HEAD + index * STEP) * fps;

  const appear = interpolate(frame, [t0, t0 + 0.45 * fps], [0, 1], {
    extrapolateLeft: "clamp",
    extrapolateRight: "clamp",
    easing: Easing.bezier(0.16, 1, 0.3, 1),
  });

  const n = row.grandNum
    ? Math.round(
        interpolate(frame, [t0 + 0.15 * fps, t0 + (0.15 + ROLL) * fps], [0, row.grandNum], {
          extrapolateLeft: "clamp",
          extrapolateRight: "clamp",
          easing: Easing.bezier(0.16, 1, 0.3, 1),
        }),
      )
    : null;

  return (
    <Interactive.Div
      name={`Row_${row.label}`}
      style={{
        display: "grid",
        gridTemplateColumns: "460px 300px 1fr",
        alignItems: "center",
        padding: "22px 0",
        borderBottom: "1px solid rgba(200,164,92,0.16)",
        opacity: appear,
        translate: interpolate(frame, [t0, t0 + 0.45 * fps], ["0px 26px", "0px 0px"], {
          extrapolateLeft: "clamp",
          extrapolateRight: "clamp",
          easing: Easing.bezier(0.16, 1, 0.3, 1),
        }),
      }}
    >
      <Interactive.Div
        name="Label"
        style={{ fontSize: 44, color: TEXT, letterSpacing: 2 }}
      >
        {row.label}
      </Interactive.Div>
      <Interactive.Div
        name="Cruiser"
        style={{ fontSize: 40, color: MUTED, fontFamily: MONO }}
      >
        {row.cruiser}
      </Interactive.Div>
      <Interactive.Div
        name="Grand"
        style={{ fontSize: 52, color: BLUE, fontFamily: MONO, fontWeight: 700 }}
      >
        {row.grandPrefix ?? ""}
        {n !== null ? n : row.grand}
        {row.grandSuffix ?? ""}
      </Interactive.Div>
    </Interactive.Div>
  );
};

export const ShipStats: React.FC<{
  /**
   * 压在实机画面上放（成片里压在虚空战上）。
   *
   * ★为什么需要这个开关★
   *   默认那个不透明的 INK 底当独立片段用没问题，但压在画面上时
   *   它会把下面的虚空战全部盖掉 —— 外层给 0.94 的 opacity 也没用，
   *   底本身是实心的，等于切了一个黑屏。而且切入的那一帧标题还没淡入，
   *   观众看到的是一整帧纯黑。
   *   叠加模式下改成会淡入的半透明遮罩：数值看得清，船也还在。
   */
  overlay?: boolean;
}> = ({ overlay = false }) => {
  const frame = useCurrentFrame();
  const { fps } = useVideoConfig();

  // 8 秒的叠加窗口：0.5s 淡入，末尾 0.6s 淡出，避免硬切回画面
  const scrim = overlay
    ? interpolate(frame, [0, 0.5 * fps, 7.4 * fps, 8 * fps], [0, 0.82, 0.82, 0], {
        extrapolateLeft: "clamp",
        extrapolateRight: "clamp",
        easing: Easing.bezier(0.16, 1, 0.3, 1),
      })
    : 1;

  return (
    <AbsoluteFill
      name="ShipStats"
      style={{
        backgroundColor: overlay ? `rgba(10,11,13,${scrim})` : INK,
        fontFamily: FONT,
        padding: "110px 150px",
        display: "flex",
        flexDirection: "column",
        justifyContent: "center",
        // 叠加模式下整体跟着遮罩一起收尾，不然表格会挂在画面上突然消失
        opacity: overlay ? Math.min(1, scrim / 0.82) : 1,
      }}
    >
      <Interactive.Div
        name="Title"
        style={{
          fontSize: 40,
          color: GOLD,
          letterSpacing: 4,
          marginBottom: 10,
          opacity: interpolate(frame, [0, 0.5 * fps], [0, 1], {
            extrapolateLeft: "clamp",
            extrapolateRight: "clamp",
            easing: Easing.bezier(0.16, 1, 0.3, 1),
          }),
        }}
      >
        座舰改装加成
      </Interactive.Div>

      <Interactive.Div
        name="Header"
        style={{
          display: "grid",
          gridTemplateColumns: "460px 300px 1fr",
          alignItems: "baseline",
          paddingBottom: 16,
          borderBottom: `2px solid ${GOLD}`,
          opacity: interpolate(frame, [0.15 * fps, 0.7 * fps], [0, 1], {
            extrapolateLeft: "clamp",
            extrapolateRight: "clamp",
            easing: Easing.bezier(0.16, 1, 0.3, 1),
          }),
        }}
      >
        <Interactive.Div name="H0" style={{ fontSize: 30, color: MUTED }}>
          相对护卫舰
        </Interactive.Div>
        <Interactive.Div name="H1" style={{ fontSize: 34, color: MUTED }}>
          巡洋舰
        </Interactive.Div>
        <Interactive.Div name="H2" style={{ fontSize: 40, color: GOLD, fontWeight: 700 }}>
          大巡洋舰
        </Interactive.Div>
      </Interactive.Div>

      {ROWS.map((r, i) => (
        <StatRow key={r.label} row={r} index={i} />
      ))}

      {/* ★叠加模式下不画脚注★
          两个原因：一是它落在字幕带上，两行字会撞在一起；
          二是它说的「改装可随时还原并退回废料」正好是旁白同一刻在念的那句，
          画面重复一遍旁白只是在抢注意力。独立片段没有旁白，那时它是必要的。 */}
      {!overlay && (
        <Sequence from={Math.round((HEAD + ROWS.length * STEP + 0.4) * fps)} layout="none">
          <Foot />
        </Sequence>
      )}
    </AbsoluteFill>
  );
};

const Foot: React.FC = () => {
  const frame = useCurrentFrame();
  const { fps } = useVideoConfig();
  return (
    <Interactive.Div
      name="Foot"
      style={{
        marginTop: 34,
        fontSize: 28,
        color: MUTED,
        letterSpacing: 2,
        opacity: interpolate(frame, [0, 0.6 * fps], [0, 1], {
          extrapolateLeft: "clamp",
          extrapolateRight: "clamp",
          easing: Easing.bezier(0.16, 1, 0.3, 1),
        }),
      }}
    >
      以上为默认值，面板里每一项都是滑条　·　改装可随时还原并退回废料
    </Interactive.Div>
  );
};
