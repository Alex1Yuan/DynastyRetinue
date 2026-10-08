using System;
using System.Collections.Generic;
using System.Globalization;
using Kingmaker;

namespace DynastyRetinue
{
    /// <summary>
    /// 自动传送的两阶段同步事务。
    /// prepare 只读预检；各端下一帧回发 ready；房主收齐后才发 commit。
    /// 任一端未就绪时统一 abort，不让成功端先写 Position / IsInGame。
    ///
    /// 常态热路径只有三个空集合和一个字符串判断；没有事务时不碰 Game、网络或实体。
    /// </summary>
    internal static class CoopAutoTransaction
    {
        private const int MaxPlayers = 6;
        private const int TimeoutTicks = 10 * 20;
        private const int RetryFrames = 60;
        private const int RearmDelayTicks = 5 * 20;

        private static int _sequence;
        private static int _outboundRetryFrames;
        private static int _timeoutFrameSkip;
        // 进入船区时 hideguards 与 spaceescort 会同时工作；每个 kind 必须独立重试。
        private static readonly Dictionary<string, int> RearmAt =
            new Dictionary<string, int>(StringComparer.Ordinal);

        private sealed class Tx
        {
            public string Id;
            public string Kind;
            public string Key;
            public string[] Members;
            public HashSet<string> MemberSet;
            public readonly Dictionary<string, string> Ready =
                new Dictionary<string, string>(StringComparer.Ordinal);
            public object Plan;
            public string Signature;
            public int StartedTick;
        }

        private static readonly Dictionary<string, Tx> Transactions =
            new Dictionary<string, Tx>(StringComparer.Ordinal);
        private static readonly Dictionary<string, string[]> ReadyReplies =
            new Dictionary<string, string[]>(StringComparer.Ordinal);
        private static readonly HashSet<string> Decisions =
            new HashSet<string>(StringComparer.Ordinal);

        internal static bool Begin(string kind, string[] payload)
        {
            if (!ValidKind(kind) || payload == null || !CoopState.IsConfirmedHost) return false;
            string key = TransactionKey(kind, payload);
            if (string.IsNullOrEmpty(key)) return false;
            int rearmAt;
            if (kind != "rescue" && RearmAt.TryGetValue(kind, out rearmAt)
                && NetworkTick() < rearmAt) return false;
            foreach (var tx in Transactions.Values)
                if (string.Equals(tx.Key, key, StringComparison.Ordinal)) return true;

            string generation;
            string[] members;
            if (!CoopState.TryGetActivePlayerGeneration(out generation, out members)) return false;

            int tick = NetworkTick();
            string id = tick.ToString(CultureInfo.InvariantCulture) + ":"
                      + (++_sequence).ToString(CultureInfo.InvariantCulture) + ":" + kind;
            var args = new string[4 + members.Length + payload.Length];
            args[0] = id;
            args[1] = kind;
            args[2] = generation;
            args[3] = members.Length.ToString(CultureInfo.InvariantCulture);
            Array.Copy(members, 0, args, 4, members.Length);
            Array.Copy(payload, 0, args, 4 + members.Length, payload.Length);
            return CoopCommand.Send("autoprepare", args);
        }

        internal static void ReceivePrepare(string[] args)
        {
            if (args == null || args.Length < 5 || !ValidKind(args[1])) return;
            int count;
            if (string.IsNullOrEmpty(args[0]) || string.IsNullOrEmpty(args[2])
                || !int.TryParse(args[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out count)
                || count < 1 || count > MaxPlayers || args.Length < 4 + count) return;

            var members = new string[count];
            Array.Copy(args, 4, members, 0, count);
            for (int i = 0; i < members.Length; i++)
            {
                if (string.IsNullOrEmpty(members[i])) return;
                if (i > 0 && string.CompareOrdinal(members[i - 1], members[i]) >= 0) return;
            }
            if (!string.Equals(args[2], CoopState.BuildPlayerGeneration(members), StringComparison.Ordinal)) return;

            string me = CoopState.LocalUserId;
            var memberSet = new HashSet<string>(members, StringComparer.Ordinal);
            if (string.IsNullOrEmpty(me) || !memberSet.Contains(me)) return;

            Tx existing;
            if (Transactions.TryGetValue(args[0], out existing))
            {
                QueueReady(existing, me);
                return;
            }

            bool membersMatch;
            bool memberStateReady = CoopState.TryActivePlayersMatch(members, out membersMatch) && membersMatch;
            var payload = new string[args.Length - 4 - count];
            Array.Copy(args, 4 + count, payload, 0, payload.Length);
            string key = TransactionKey(args[1], payload);
            if (string.IsNullOrEmpty(key)) return;

            object plan = null;
            string signature = "";
            string failure = memberStateReady ? "" : "本端玩家名单尚未追上房主";
            bool ready = memberStateReady
                      && TryPrepare(args[1], payload, out plan, out signature, out failure);
            var tx = new Tx
            {
                Id = args[0], Kind = args[1], Key = key, Members = members,
                MemberSet = memberSet, Plan = ready ? plan : null,
                Signature = ready ? signature : "", StartedTick = NetworkTick()
            };
            Transactions[tx.Id] = tx;
            if (tx.Kind == "placeguards" || tx.Kind == "hideguards")
                RetinueLifecycle.CancelPendingPlacement();
            QueueReady(tx, me);
            if (!ready) Main.Log("[合作事务] " + tx.Kind + " 预检未通过，不写状态：" + failure);
        }

        private static void QueueReady(Tx tx, string me)
        {
            bool ok = tx != null && tx.Plan != null;
            ReadyReplies[tx.Id] = new[] { tx.Id, me, ok ? "1" : "0", ok ? tx.Signature : "" };
        }

        internal static void ReceiveReady(string[] args)
        {
            if (args == null || args.Length < 4) return;
            Tx tx;
            if (!Transactions.TryGetValue(args[0], out tx) || !tx.MemberSet.Contains(args[1])) return;
            if (args[2] != "0" && args[2] != "1") return;
            tx.Ready[args[1]] = args[2] == "1" ? args[3] : "";
            if (CoopState.IsConfirmedHost) Decisions.Add(tx.Id);
        }

        internal static void ReceiveCommit(string[] args)
        {
            if (args == null || args.Length < 1) return;
            Tx tx;
            if (!Transactions.TryGetValue(args[0], out tx) || !AllReady(tx)) return;

            string failure;
            if (!CanCommitInHandler(tx.Kind, tx.Plan, out failure))
            {
                Remove(tx.Id);
                Abort(tx.Kind);
                Main.Log("[合作事务] commit 到达时状态已变化，未写入：" + failure);
                return;
            }

            try
            {
                Commit(tx.Kind, tx.Plan);
                if (tx.Kind == "placeguards" || tx.Kind == "hideguards" || tx.Kind == "spaceescort")
                    RearmAt.Remove(tx.Kind);
            }
            catch (Exception e) { Main.LogError("[合作事务] " + tx.Kind + " 提交异常：" + e); }
            finally { Remove(tx.Id); }
        }

        internal static void ReceiveAbort(string[] args)
        {
            if (args == null || args.Length < 2 || !ValidKind(args[1])) return;
            string id = args[0], kind = args[1];
            Tx tx;
            if (Transactions.TryGetValue(id, out tx)
                && !string.Equals(tx.Kind, kind, StringComparison.Ordinal)) return;
            Remove(id);
            Abort(kind);
        }

        internal static void Tick()
        {
            if (ReadyReplies.Count == 0 && Decisions.Count == 0 && Transactions.Count == 0
                && RearmAt.Count == 0) return;

            bool timeoutDue = ++_timeoutFrameSkip >= RetryFrames;
            if (timeoutDue)
            {
                _timeoutFrameSkip = 0;
                int rearmNow = NetworkTick();
                var dueKinds = new List<string>();
                foreach (var pair in RearmAt)
                    if (rearmNow >= pair.Value) dueKinds.Add(pair.Key);
                dueKinds.Sort(StringComparer.Ordinal);
                foreach (string kind in dueKinds)
                {
                    RearmAt.Remove(kind);
                    // 所有端保留 pending；真正发起仍由各业务自己的房主闸限定。
                    Rearm(kind);
                    if (CoopState.IsConfirmedHost)
                        Main.Log("[合作事务] " + kind + " 固定 5 秒重试已到，重新规划。");
                }
            }

            bool outboundDue = _outboundRetryFrames <= 0;
            if (_outboundRetryFrames > 0) _outboundRetryFrames--;
            bool hasOutbound = ReadyReplies.Count > 0 || Decisions.Count > 0;
            if ((!hasOutbound || !outboundDue) && !timeoutDue) return;

            bool shared;
            if (!CoopState.TryGetSharedGameplayRequired(out shared)) return;
            if (!shared) { ClearProtocolState(); return; }

            if (ReadyReplies.Count > 0 && outboundDue)
            {
                bool failed = false;
                var ids = new List<string>(ReadyReplies.Keys);
                ids.Sort(StringComparer.Ordinal);
                foreach (string id in ids)
                {
                    string[] reply;
                    if (ReadyReplies.TryGetValue(id, out reply) && CoopCommand.Send("autoready", reply))
                        ReadyReplies.Remove(id);
                    else failed = true;
                }
                if (failed) _outboundRetryFrames = RetryFrames;
            }

            if (Decisions.Count > 0 && outboundDue && CoopState.IsConfirmedHost)
            {
                bool failed = false;
                var ids = new List<string>(Decisions);
                ids.Sort(StringComparer.Ordinal);
                foreach (string id in ids)
                {
                    Tx tx;
                    if (!Transactions.TryGetValue(id, out tx)) { Decisions.Remove(id); continue; }
                    if (HasFailure(tx))
                    {
                        if (CoopCommand.Send("autoabort", id, tx.Kind)) Decisions.Remove(id);
                        else failed = true;
                    }
                    else if (AllReady(tx))
                    {
                        bool membersMatch;
                        string reason;
                        if (!CoopState.TryActivePlayersMatch(tx.Members, out membersMatch)
                            || !membersMatch || !CanHostCommit(tx.Kind, tx.Plan, out reason))
                        {
                            if (CoopCommand.Send("autoabort", id, tx.Kind)) Decisions.Remove(id);
                            else failed = true;
                        }
                        else if (CoopCommand.Send("autocommit", id)) Decisions.Remove(id);
                        else failed = true;
                    }
                    else Decisions.Remove(id);
                }
                if (failed) _outboundRetryFrames = RetryFrames;
            }

            if (!timeoutDue || !CoopState.IsConfirmedHost) return;
            int now = NetworkTick();
            bool timeoutSendFailed = false;
            var timed = new List<string>(Transactions.Keys);
            foreach (string id in timed)
            {
                Tx tx;
                if (!Transactions.TryGetValue(id, out tx)) continue;
                int elapsed = now - tx.StartedTick;
                if (elapsed < 0 || elapsed > TimeoutTicks)
                {
                    if (!CoopCommand.Send("autoabort", id, tx.Kind)) timeoutSendFailed = true;
                }
            }
            if (timeoutSendFailed) _outboundRetryFrames = RetryFrames;
        }

        internal static void DelayBegin(string kind)
        {
            ScheduleRearm(kind, NetworkTick(), "发起条件尚未稳定");
        }

        private static void Abort(string kind)
        {
            ScheduleRearm(kind, NetworkTick(), "事务已取消");
        }

        private static void ScheduleRearm(string kind, int now, string reason)
        {
            if (kind != "placeguards" && kind != "hideguards" && kind != "spaceescort") return;
            RearmAt[kind] = now + RearmDelayTicks;
            Main.Log("[合作事务] " + kind + " " + reason + "；5 秒后重试。");
        }

        private static void Rearm(string kind)
        {
            if (kind == "spaceescort") { SpaceEscortService.RearmPending(); return; }
            RetinueLifecycle.RearmPlacement();
        }

        private static bool TryPrepare(string kind, string[] payload, out object plan,
                                       out string signature, out string failure)
        {
            if (kind == "placeguards") return RetinueLifecycle.TryPrepareSynchronizedPlacement(payload, out plan, out signature, out failure);
            if (kind == "hideguards") return RetinueLifecycle.TryPrepareSynchronizedHide(payload, out plan, out signature, out failure);
            if (kind == "rescue") return StuckWatch.TryPrepareSynchronizedRescue(payload, out plan, out signature, out failure);
            if (kind == "spaceescort") return SpaceEscortService.TryPrepareSynchronizedEscort(payload, out plan, out signature, out failure);
            plan = null; signature = ""; failure = "未知事务类型"; return false;
        }

        private static bool CanHostCommit(string kind, object plan, out string failure)
        {
            if (kind == "placeguards" || kind == "hideguards")
                return RetinueLifecycle.CanCommitSynchronizedPlan(plan, out failure);
            if (kind == "rescue") return StuckWatch.CanCommitSynchronizedRescue(plan, out failure);
            if (kind == "spaceescort") return SpaceEscortService.CanCommitSynchronizedEscort(plan, out failure);
            failure = "未知事务类型"; return false;
        }

        private static bool CanCommitInHandler(string kind, object plan, out string failure)
        {
            if (kind == "placeguards" || kind == "hideguards")
                return RetinueLifecycle.CanCommitSynchronizedPlanInHandler(plan, out failure);
            if (kind == "rescue") return StuckWatch.CanCommitSynchronizedRescue(plan, out failure);
            if (kind == "spaceescort") return SpaceEscortService.CanCommitSynchronizedEscortInHandler(plan, out failure);
            failure = "未知事务类型"; return false;
        }

        private static void Commit(string kind, object plan)
        {
            if (kind == "placeguards") { RetinueLifecycle.CommitSynchronizedPlacement(plan); return; }
            if (kind == "hideguards") { RetinueLifecycle.CommitSynchronizedHide(plan); return; }
            if (kind == "rescue") { StuckWatch.CommitSynchronizedRescue(plan); return; }
            if (kind == "spaceescort") { SpaceEscortService.CommitSynchronizedEscort(plan); return; }
            throw new InvalidOperationException("未知事务类型 " + kind);
        }

        private static bool HasFailure(Tx tx)
        {
            foreach (string signature in tx.Ready.Values)
                if (string.IsNullOrEmpty(signature)
                    || !string.Equals(signature, tx.Signature, StringComparison.Ordinal)) return true;
            return false;
        }

        private static bool AllReady(Tx tx)
        {
            if (tx == null || tx.Plan == null || string.IsNullOrEmpty(tx.Signature)
                || tx.Ready.Count != tx.MemberSet.Count) return false;
            foreach (string member in tx.MemberSet)
            {
                string signature;
                if (!tx.Ready.TryGetValue(member, out signature)
                    || !string.Equals(signature, tx.Signature, StringComparison.Ordinal)) return false;
            }
            return true;
        }

        private static bool ValidKind(string kind)
        {
            return kind == "placeguards" || kind == "hideguards" || kind == "rescue"
                || kind == "spaceescort";
        }

        private static string TransactionKey(string kind, string[] payload)
        {
            if (kind == "rescue")
                return payload != null && payload.Length > 0 && !string.IsNullOrEmpty(payload[0])
                    ? "rescue:" + payload[0] : "";
            if (kind == "spaceescort")
            {
                if (!SpaceEscortService.ValidTransactionHeader(payload)) return "";
                // 同一 encounter 的 spawn/cleanup 共用 key，禁止两个写实体事务并发。
                return "spaceescort:" + payload[2];
            }
            return kind;
        }

        private static void Remove(string id)
        {
            Transactions.Remove(id);
            ReadyReplies.Remove(id);
            Decisions.Remove(id);
        }

        private static void ClearProtocolState()
        {
            var rearmKinds = new HashSet<string>(RearmAt.Keys, StringComparer.Ordinal);
            foreach (var tx in Transactions.Values)
                if (tx.Kind == "placeguards" || tx.Kind == "hideguards" || tx.Kind == "spaceescort")
                    rearmKinds.Add(tx.Kind);
            Transactions.Clear();
            ReadyReplies.Clear();
            Decisions.Clear();
            _outboundRetryFrames = 0;
            _timeoutFrameSkip = 0;
            RearmAt.Clear();
            foreach (string kind in rearmKinds) Rearm(kind);
        }

        internal static string Status()
        {
            var kinds = new List<string>(RearmAt.Keys);
            kinds.Sort(StringComparer.Ordinal);
            return "active=" + Transactions.Count.ToString(CultureInfo.InvariantCulture)
                 + " readyOut=" + ReadyReplies.Count.ToString(CultureInfo.InvariantCulture)
                 + " decisions=" + Decisions.Count.ToString(CultureInfo.InvariantCulture)
                 + (kinds.Count == 0 ? "" : " rearm=" + string.Join(",", kinds.ToArray()));
        }

        private static int NetworkTick()
        {
            try { return Game.Instance.RealTimeController.CurrentNetworkTick; }
            catch { return 0; }
        }
    }
}
