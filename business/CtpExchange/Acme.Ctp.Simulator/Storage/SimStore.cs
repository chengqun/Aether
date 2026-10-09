using Acme.Ctp.Domain;
using Acme.Persistence;
using Microsoft.Data.Sqlite;

namespace Acme.Ctp.Simulator.Storage;

/// <summary>
/// SQLite 落盘的 <see cref="ISimStore"/>（继承仓库共享的 <see cref="SqliteStoreBase"/>：
/// 连接串校验 / busy_timeout / WAL / 幂等建表 / 迁移 hook 全部现成）。
/// <para>6 张表：<c>sim_account</c> / <c>sim_position</c> / <c>sim_order</c> / <c>sim_trade</c> / <c>sim_instrument</c> / <c>sim_tick</c>
/// （成交 tick——真实交易消息，2026-09-28 起：交易所模式每笔成交的完整快照，重启恢复行情状态的读侧）。
/// 前 5 张用 <c>INSERT OR REPLACE</c> 语义的 upsert（账户/持仓覆盖式；订单/成交以 (account, orderRef/tradeId) 为主键幂等重写）；
/// <c>sim_tick</c> 追加式日志（无主键——按 rowid 序即时间序）。</para>
/// <para><b>不像真柜台那样加密</b>：仿真数据无敏感内容（不是真资金、不是真凭据），不引入加密成本。</para>
/// </summary>
public sealed class SimStore : SqliteStoreBase, ISimStore
{
    private const string CreateSql = """
        CREATE TABLE IF NOT EXISTS sim_account (
            account_id       TEXT PRIMARY KEY,
            static_balance   REAL NOT NULL,
            curr_margin      REAL NOT NULL,
            frozen_margin    REAL NOT NULL,
            frozen_cash      REAL NOT NULL,
            commission       REAL NOT NULL,
            close_profit     REAL NOT NULL,
            position_profit  REAL NOT NULL,
            password_hash    TEXT NOT NULL DEFAULT '',
            broker_id        TEXT NOT NULL DEFAULT '',
            front_password_hash TEXT NOT NULL DEFAULT ''
        );
        CREATE TABLE IF NOT EXISTS sim_position (
            account_id    TEXT NOT NULL,
            instrument_id TEXT NOT NULL,
            exchange_id   TEXT NOT NULL DEFAULT '',
            direction     TEXT NOT NULL,
            today         INTEGER NOT NULL,
            yesterday     INTEGER NOT NULL,
            cost          REAL NOT NULL,
            margin        REAL NOT NULL,
            PRIMARY KEY (account_id, instrument_id, direction)
        );
        CREATE TABLE IF NOT EXISTS sim_order (
            account_id           TEXT NOT NULL,
            order_ref            TEXT NOT NULL,
            instrument_id        TEXT NOT NULL,
            exchange_id          TEXT NOT NULL DEFAULT '',
            front_id             INTEGER NOT NULL,
            session_id           INTEGER NOT NULL,
            order_sys_id         TEXT NOT NULL DEFAULT '',
            direction            INTEGER NOT NULL,
            offset_flag          INTEGER NOT NULL,
            price_type           INTEGER NOT NULL,
            limit_price          REAL NOT NULL,
            volume_total_original INTEGER NOT NULL,
            volume_traded        INTEGER NOT NULL,
            volume_total         INTEGER NOT NULL,
            order_status         INTEGER NOT NULL,
            insert_time          TEXT NOT NULL,
            update_time          TEXT NOT NULL,
            status_msg           TEXT NOT NULL,
            PRIMARY KEY (account_id, order_ref)
        );
        CREATE TABLE IF NOT EXISTS sim_trade (
            account_id    TEXT NOT NULL,
            trade_id      TEXT NOT NULL,
            order_ref     TEXT NOT NULL,
            order_sys_id  TEXT NOT NULL DEFAULT '',
            instrument_id TEXT NOT NULL,
            direction     INTEGER NOT NULL,
            offset_flag   INTEGER NOT NULL,
            price         REAL NOT NULL,
            volume        INTEGER NOT NULL,
            trade_time    TEXT NOT NULL,
            PRIMARY KEY (account_id, trade_id)
        );
        CREATE TABLE IF NOT EXISTS sim_instrument (
            instrument_id   TEXT PRIMARY KEY,
            exchange_id     TEXT NOT NULL DEFAULT '',
            volume_multiple REAL NOT NULL,
            margin_rate     REAL NOT NULL,
            price_tick      REAL NOT NULL,
            base_price      REAL NOT NULL,
            pre_settlement  REAL NOT NULL,
            commission_rate REAL NOT NULL,
            limit_rate      REAL NOT NULL,
            maker_rate      REAL,
            taker_rate      REAL,
            is_spot         INTEGER NOT NULL DEFAULT 0,
            supply_limit_lots INTEGER
        );
        CREATE TABLE IF NOT EXISTS sim_spot_minted (
            account_id    TEXT NOT NULL,
            instrument_id TEXT NOT NULL,
            minted_lots   INTEGER NOT NULL,
            PRIMARY KEY (account_id, instrument_id)
        );
        CREATE TABLE IF NOT EXISTS sim_quote_minted (
            account_id    TEXT PRIMARY KEY,
            minted_atoms  INTEGER NOT NULL
        );
        CREATE TABLE IF NOT EXISTS sim_tick (
            instrument_id   TEXT NOT NULL,
            trading_day     TEXT NOT NULL DEFAULT '',
            action_day      TEXT NOT NULL DEFAULT '',
            update_time     TEXT NOT NULL,
            update_millisec INTEGER NOT NULL,
            last_price      REAL NOT NULL,
            volume          INTEGER NOT NULL,
            bid_price       REAL NOT NULL,
            bid_volume      INTEGER NOT NULL,
            ask_price       REAL NOT NULL,
            ask_volume      INTEGER NOT NULL,
            open_interest   REAL NOT NULL,
            open_price      REAL NOT NULL,
            highest         REAL NOT NULL,
            lowest          REAL NOT NULL,
            pre_settlement  REAL NOT NULL
        );
        CREATE INDEX IF NOT EXISTS idx_sim_tick_instrument ON sim_tick(instrument_id);
        """;

    public SimStore(string connectionString) : base(connectionString) { }

    public static SimStore ForFile(string dbPath) => new(BuildConnectionString(dbPath));

    public Task InitializeAsync(CancellationToken ct = default) => EnsureSchemaAsync(CreateSql, ct);

    /// <summary>存量库幂等补列（2026-09-28 账号系统：密码哈希 + 经纪商元数据；
    /// 2026-10-05 BTC 对标：sim_instrument 的 maker/taker 差异化费率——NULL = 回退单一费率；
    /// 2026-10-07 批 4 接线：sim_instrument 的 is_spot/supply_limit_lots——**is_spot 不落盘则重启后
    /// 现货仪器退化为期货**（Credit/Burn 全被「非现货」拒），铸造台账表由 CreateSql 幂等建；
    /// 2026-10-09 双密码域：sim_account 的 front_password_hash——CTP 前置域哈希，两域并存各自补设）。</summary>
    protected override async Task MigrateAsync(SqliteConnection connection, CancellationToken ct)
    {
        await SqliteMigrations.AddColumnAsync(connection, "sim_account", "password_hash", "TEXT NOT NULL DEFAULT ''", ct);
        await SqliteMigrations.AddColumnAsync(connection, "sim_account", "broker_id", "TEXT NOT NULL DEFAULT ''", ct);
        await SqliteMigrations.AddColumnAsync(connection, "sim_account", "front_password_hash", "TEXT NOT NULL DEFAULT ''", ct);
        await SqliteMigrations.AddColumnAsync(connection, "sim_instrument", "maker_rate", "REAL", ct);
        await SqliteMigrations.AddColumnAsync(connection, "sim_instrument", "taker_rate", "REAL", ct);
        await SqliteMigrations.AddColumnAsync(connection, "sim_instrument", "is_spot", "INTEGER NOT NULL DEFAULT 0", ct);
        await SqliteMigrations.AddColumnAsync(connection, "sim_instrument", "supply_limit_lots", "INTEGER", ct);
    }

    public async Task<IReadOnlyList<SimAccountSnapshot>> LoadAccountsAsync(CancellationToken ct = default)
    {
        await EnsureSchemaAsync(CreateSql, ct);
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT account_id, static_balance, curr_margin, frozen_margin, frozen_cash, commission, close_profit, position_profit, password_hash, broker_id, front_password_hash FROM sim_account";
        var list = new List<SimAccountSnapshot>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            list.Add(new SimAccountSnapshot(
                r.GetString(0), r.GetDouble(1), r.GetDouble(2), r.GetDouble(3),
                r.GetDouble(4), r.GetDouble(5), r.GetDouble(6), r.GetDouble(7),
                NullIfEmpty(r.GetString(8)), NullIfEmpty(r.GetString(9)), NullIfEmpty(r.GetString(10))));
        }
        return list;
    }

    private static string? NullIfEmpty(string v) => string.IsNullOrEmpty(v) ? null : v;

    private static string EmptyIfNull(string? v) => v ?? "";

    public async Task<IReadOnlyList<SimPositionSnapshot>> LoadPositionsAsync(string accountId, CancellationToken ct = default)
    {
        await EnsureSchemaAsync(CreateSql, ct);
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT instrument_id, exchange_id, direction, today, yesterday, cost, margin FROM sim_position WHERE account_id = @a";
        cmd.Parameters.AddWithValue("@a", accountId);
        var list = new List<SimPositionSnapshot>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            list.Add(new SimPositionSnapshot(
                r.GetString(0), r.GetString(1), r.GetString(2),
                r.GetInt32(3), r.GetInt32(4), r.GetDouble(5), r.GetDouble(6)));
        }
        return list;
    }

    public async Task<IReadOnlyList<CtpOrder>> LoadOrdersAsync(string accountId, CancellationToken ct = default)
    {
        await EnsureSchemaAsync(CreateSql, ct);
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT instrument_id, exchange_id, order_ref, front_id, session_id, order_sys_id, direction, offset_flag, price_type, limit_price, volume_total_original, volume_traded, volume_total, order_status, insert_time, update_time, status_msg FROM sim_order WHERE account_id = @a ORDER BY rowid";
        cmd.Parameters.AddWithValue("@a", accountId);
        var list = new List<CtpOrder>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            list.Add(new CtpOrder
            {
                InstrumentId = r.GetString(0),
                ExchangeId = r.GetString(1),
                OrderRef = r.GetString(2),
                FrontId = r.GetInt32(3),
                SessionId = r.GetInt32(4),
                OrderSysId = r.GetString(5),
                Direction = (CtpDirection)(byte)r.GetInt32(6),
                OffsetFlag = (CtpOffsetFlag)(byte)r.GetInt32(7),
                PriceType = (CtpOrderPriceType)(byte)r.GetInt32(8),
                LimitPrice = r.GetDouble(9),
                VolumeTotalOriginal = r.GetInt32(10),
                VolumeTraded = r.GetInt32(11),
                VolumeTotal = r.GetInt32(12),
                OrderStatusRaw = (byte)r.GetInt32(13),
                InsertTime = r.GetString(14),
                UpdateTime = r.GetString(15),
                StatusMsg = r.GetString(16),
            });
        }
        return list;
    }

    public async Task<IReadOnlyList<SimInstrument>> LoadInstrumentsAsync(CancellationToken ct = default)
    {
        await EnsureSchemaAsync(CreateSql, ct);
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT instrument_id, exchange_id, volume_multiple, margin_rate, price_tick, base_price, pre_settlement, commission_rate, limit_rate, maker_rate, taker_rate, is_spot, supply_limit_lots FROM sim_instrument";
        var list = new List<SimInstrument>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            list.Add(new SimInstrument
            {
                InstrumentId = r.GetString(0),
                ExchangeId = r.GetString(1),
                VolumeMultiple = r.GetDouble(2),
                MarginRate = r.GetDouble(3),
                PriceTick = r.GetDouble(4),
                BasePrice = r.GetDouble(5),
                PreSettlement = r.GetDouble(6),
                CommissionRate = r.GetDouble(7),
                LimitRate = r.GetDouble(8),
                // 旧行 NULL = 未配置差异化费率 → 回退单一费率（CTP 双边同费形态）
                MakerCommissionRate = r.IsDBNull(9) ? null : r.GetDouble(9),
                TakerCommissionRate = r.IsDBNull(10) ? null : r.GetDouble(10),
                IsSpot = SafeGetBoolean(r, 11),
                SupplyLimitLots = r.IsDBNull(12) ? null : r.GetInt32(12),
            });
        }
        return list;
    }

    /// <summary>is_spot 兼容读：旧行/手工行可能存 0/1 以外的值（TEXT "true" 等）——非 0 即真。</summary>
    private static bool SafeGetBoolean(Microsoft.Data.Sqlite.SqliteDataReader r, int ordinal)
        => !r.IsDBNull(ordinal) && r.GetInt32(ordinal) != 0;

    public async Task<IReadOnlyList<SpotMintedSnapshot>> LoadSpotMintedAsync(CancellationToken ct = default)
    {
        await EnsureSchemaAsync(CreateSql, ct);
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT account_id, instrument_id, minted_lots FROM sim_spot_minted";
        var list = new List<SpotMintedSnapshot>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new SpotMintedSnapshot(r.GetString(0), r.GetString(1), r.GetInt32(2)));
        return list;
    }

    public async Task<IReadOnlyList<QuoteMintedSnapshot>> LoadQuoteMintedAsync(CancellationToken ct = default)
    {
        await EnsureSchemaAsync(CreateSql, ct);
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT account_id, minted_atoms FROM sim_quote_minted";
        var list = new List<QuoteMintedSnapshot>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new QuoteMintedSnapshot(r.GetString(0), r.GetInt64(1)));
        return list;
    }

    public async Task SaveQuoteMintedAsync(string accountId, long mintedAtoms, CancellationToken ct = default)
    {
        await EnsureSchemaAsync(CreateSql, ct);
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO sim_quote_minted (account_id, minted_atoms)
            VALUES (@a, @m)
            ON CONFLICT(account_id) DO UPDATE SET minted_atoms=excluded.minted_atoms
            """;
        cmd.Parameters.AddWithValue("@a", accountId);
        cmd.Parameters.AddWithValue("@m", mintedAtoms);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task SaveSpotMintedAsync(string instrumentId, string accountId, int mintedLots, CancellationToken ct = default)
    {
        await EnsureSchemaAsync(CreateSql, ct);
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO sim_spot_minted (account_id, instrument_id, minted_lots)
            VALUES (@a, @i, @m)
            ON CONFLICT(account_id, instrument_id) DO UPDATE SET minted_lots=excluded.minted_lots
            """;
        cmd.Parameters.AddWithValue("@a", accountId);
        cmd.Parameters.AddWithValue("@i", instrumentId);
        cmd.Parameters.AddWithValue("@m", mintedLots);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task SaveAccountAsync(SimAccountSnapshot s, CancellationToken ct = default)
    {
        await EnsureSchemaAsync(CreateSql, ct);
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO sim_account (account_id, static_balance, curr_margin, frozen_margin, frozen_cash, commission, close_profit, position_profit, password_hash, broker_id, front_password_hash)
            VALUES (@a, @s, @c, @f, @fc, @cm, @cp, @pp, @ph, @b, @fph)
            ON CONFLICT(account_id) DO UPDATE SET
                static_balance=excluded.static_balance, curr_margin=excluded.curr_margin,
                frozen_margin=excluded.frozen_margin, frozen_cash=excluded.frozen_cash,
                commission=excluded.commission, close_profit=excluded.close_profit,
                position_profit=excluded.position_profit,
                password_hash=excluded.password_hash, broker_id=excluded.broker_id,
                front_password_hash=excluded.front_password_hash
            """;
        cmd.Parameters.AddWithValue("@a", s.AccountId);
        cmd.Parameters.AddWithValue("@s", s.StaticBalance);
        cmd.Parameters.AddWithValue("@c", s.CurrMargin);
        cmd.Parameters.AddWithValue("@f", s.FrozenMargin);
        cmd.Parameters.AddWithValue("@fc", s.FrozenCash);
        cmd.Parameters.AddWithValue("@cm", s.Commission);
        cmd.Parameters.AddWithValue("@cp", s.CloseProfit);
        cmd.Parameters.AddWithValue("@pp", s.PositionProfit);
        cmd.Parameters.AddWithValue("@ph", EmptyIfNull(s.PasswordHash));
        cmd.Parameters.AddWithValue("@b", EmptyIfNull(s.BrokerId));
        cmd.Parameters.AddWithValue("@fph", EmptyIfNull(s.FrontPasswordHash));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>覆盖式写入该账户全部持仓（先删后插——已平掉的合约不会留残行）。</summary>
    public async Task SavePositionsAsync(string accountId, IReadOnlyList<SimPositionSnapshot> positions, CancellationToken ct = default)
    {
        await EnsureSchemaAsync(CreateSql, ct);
        await using var conn = await OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        await using (var del = conn.CreateCommand())
        {
            del.Transaction = (SqliteTransaction)tx;
            del.CommandText = "DELETE FROM sim_position WHERE account_id = @a";
            del.Parameters.AddWithValue("@a", accountId);
            await del.ExecuteNonQueryAsync(ct);
        }

        foreach (var p in positions)
        {
            await using var ins = conn.CreateCommand();
            ins.Transaction = (SqliteTransaction)tx;
            ins.CommandText = "INSERT OR REPLACE INTO sim_position (account_id, instrument_id, exchange_id, direction, today, yesterday, cost, margin) VALUES (@a, @i, @e, @d, @t, @y, @c, @m)";
            ins.Parameters.AddWithValue("@a", accountId);
            ins.Parameters.AddWithValue("@i", p.InstrumentId);
            ins.Parameters.AddWithValue("@e", p.ExchangeId);
            ins.Parameters.AddWithValue("@d", p.Direction);
            ins.Parameters.AddWithValue("@t", p.Today);
            ins.Parameters.AddWithValue("@y", p.Yesterday);
            ins.Parameters.AddWithValue("@c", p.Cost);
            ins.Parameters.AddWithValue("@m", p.Margin);
            await ins.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
    }

    public async Task<IReadOnlyList<string>> LoadTradeIdsAsync(string accountId, CancellationToken ct = default)
    {
        await EnsureSchemaAsync(CreateSql, ct);
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT trade_id FROM sim_trade WHERE account_id = @a";
        cmd.Parameters.AddWithValue("@a", accountId);
        var list = new List<string>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(r.GetString(0));
        return list;
    }

    public async Task SaveOrderAsync(string accountId, CtpOrder o, CancellationToken ct = default)
    {
        await EnsureSchemaAsync(CreateSql, ct);
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO sim_order (account_id, order_ref, instrument_id, exchange_id, front_id, session_id, order_sys_id,
                direction, offset_flag, price_type, limit_price, volume_total_original, volume_traded, volume_total,
                order_status, insert_time, update_time, status_msg)
            VALUES (@a, @r, @i, @e, @f, @s, @os, @d, @of, @pt, @lp, @vt, @td, @tl, @st, @it, @ut, @sm)
            ON CONFLICT(account_id, order_ref) DO UPDATE SET
                order_sys_id=excluded.order_sys_id, volume_traded=excluded.volume_traded,
                volume_total=excluded.volume_total, order_status=excluded.order_status,
                update_time=excluded.update_time, status_msg=excluded.status_msg
            """;
        cmd.Parameters.AddWithValue("@a", accountId);
        cmd.Parameters.AddWithValue("@r", o.OrderRef);
        cmd.Parameters.AddWithValue("@i", o.InstrumentId);
        cmd.Parameters.AddWithValue("@e", o.ExchangeId);
        cmd.Parameters.AddWithValue("@f", o.FrontId);
        cmd.Parameters.AddWithValue("@s", o.SessionId);
        cmd.Parameters.AddWithValue("@os", o.OrderSysId);
        cmd.Parameters.AddWithValue("@d", (int)o.Direction);
        cmd.Parameters.AddWithValue("@of", (int)o.OffsetFlag);
        cmd.Parameters.AddWithValue("@pt", (int)o.PriceType);
        cmd.Parameters.AddWithValue("@lp", o.LimitPrice);
        cmd.Parameters.AddWithValue("@vt", o.VolumeTotalOriginal);
        cmd.Parameters.AddWithValue("@td", o.VolumeTraded);
        cmd.Parameters.AddWithValue("@tl", o.VolumeTotal);
        cmd.Parameters.AddWithValue("@st", (int)o.OrderStatusRaw);
        cmd.Parameters.AddWithValue("@it", o.InsertTime);
        cmd.Parameters.AddWithValue("@ut", o.UpdateTime);
        cmd.Parameters.AddWithValue("@sm", o.StatusMsg);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task SaveTradeAsync(string accountId, CtpTrade t, CancellationToken ct = default)
    {
        await EnsureSchemaAsync(CreateSql, ct);
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT OR REPLACE INTO sim_trade (account_id, trade_id, order_ref, order_sys_id, instrument_id,
                direction, offset_flag, price, volume, trade_time)
            VALUES (@a, @t, @r, @os, @i, @d, @of, @p, @v, @tt)
            """;
        cmd.Parameters.AddWithValue("@a", accountId);
        cmd.Parameters.AddWithValue("@t", t.TradeId);
        cmd.Parameters.AddWithValue("@r", t.OrderRef);
        cmd.Parameters.AddWithValue("@os", t.OrderSysId);
        cmd.Parameters.AddWithValue("@i", t.InstrumentId);
        cmd.Parameters.AddWithValue("@d", (int)t.Direction);
        cmd.Parameters.AddWithValue("@of", (int)t.OffsetFlag);
        cmd.Parameters.AddWithValue("@p", t.Price);
        cmd.Parameters.AddWithValue("@v", t.Volume);
        cmd.Parameters.AddWithValue("@tt", t.TradeTime);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task SaveInstrumentAsync(SimInstrument i, CancellationToken ct = default)
    {
        await EnsureSchemaAsync(CreateSql, ct);
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();        cmd.CommandText = """
            INSERT INTO sim_instrument (instrument_id, exchange_id, volume_multiple, margin_rate, price_tick, base_price, pre_settlement, commission_rate, limit_rate, maker_rate, taker_rate, is_spot, supply_limit_lots)
            VALUES (@i, @e, @vm, @mr, @pt, @bp, @ps, @cr, @lr, @mk, @tk, @sp, @sl)
            ON CONFLICT(instrument_id) DO UPDATE SET
                exchange_id=excluded.exchange_id, volume_multiple=excluded.volume_multiple,
                margin_rate=excluded.margin_rate, price_tick=excluded.price_tick,
                base_price=excluded.base_price, pre_settlement=excluded.pre_settlement,
                commission_rate=excluded.commission_rate, limit_rate=excluded.limit_rate,
                maker_rate=excluded.maker_rate, taker_rate=excluded.taker_rate,
                is_spot=excluded.is_spot, supply_limit_lots=excluded.supply_limit_lots
            """;
        cmd.Parameters.AddWithValue("@i", i.InstrumentId);
        cmd.Parameters.AddWithValue("@e", i.ExchangeId);
        cmd.Parameters.AddWithValue("@vm", i.VolumeMultiple);
        cmd.Parameters.AddWithValue("@mr", i.MarginRate);
        cmd.Parameters.AddWithValue("@pt", i.PriceTick);
        cmd.Parameters.AddWithValue("@bp", i.BasePrice);
        cmd.Parameters.AddWithValue("@ps", i.PreSettlement);
        cmd.Parameters.AddWithValue("@cr", i.CommissionRate);
        cmd.Parameters.AddWithValue("@lr", i.LimitRate);
        cmd.Parameters.AddWithValue("@mk", (object?)i.MakerCommissionRate ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@tk", (object?)i.TakerCommissionRate ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@sp", i.IsSpot ? 1 : 0);
        cmd.Parameters.AddWithValue("@sl", (object?)i.SupplyLimitLots ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>追加一条成交 tick（真实交易消息——交易所模式每笔成交的完整快照）。追加式日志，无主键。</summary>
    public async Task SaveTickAsync(CtpTick t, CancellationToken ct = default)
    {
        await EnsureSchemaAsync(CreateSql, ct);
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO sim_tick (instrument_id, trading_day, action_day, update_time, update_millisec,
                last_price, volume, bid_price, bid_volume, ask_price, ask_volume,
                open_interest, open_price, highest, lowest, pre_settlement)
            VALUES (@i, @td, @ad, @ut, @ums, @lp, @v, @bp, @bv, @ap, @av, @oi, @op, @h, @l, @ps)
            """;
        cmd.Parameters.AddWithValue("@i", t.InstrumentId);
        cmd.Parameters.AddWithValue("@td", t.TradingDay);
        cmd.Parameters.AddWithValue("@ad", t.ActionDay);
        cmd.Parameters.AddWithValue("@ut", t.UpdateTime);
        cmd.Parameters.AddWithValue("@ums", t.UpdateMillisec);
        cmd.Parameters.AddWithValue("@lp", t.LastPrice);
        cmd.Parameters.AddWithValue("@v", t.Volume);
        cmd.Parameters.AddWithValue("@bp", t.BidPrice);
        cmd.Parameters.AddWithValue("@bv", t.BidVolume);
        cmd.Parameters.AddWithValue("@ap", t.AskPrice);
        cmd.Parameters.AddWithValue("@av", t.AskVolume);
        cmd.Parameters.AddWithValue("@oi", t.OpenInterest);
        cmd.Parameters.AddWithValue("@op", t.OpenPrice);
        cmd.Parameters.AddWithValue("@h", t.Highest);
        cmd.Parameters.AddWithValue("@l", t.Lowest);
        cmd.Parameters.AddWithValue("@ps", t.PreSettlement);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>读某合约最近 <paramref name="limit"/> 条成交 tick（<b>时间升序</b>——末位 = 最新；重启恢复取 limit=1）。</summary>
    public async Task<IReadOnlyList<CtpTick>> LoadRecentTicksAsync(string instrumentId, int limit, CancellationToken ct = default)
    {
        await EnsureSchemaAsync(CreateSql, ct);
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT instrument_id, trading_day, action_day, update_time, update_millisec,
                last_price, volume, bid_price, bid_volume, ask_price, ask_volume,
                open_interest, open_price, highest, lowest, pre_settlement
            FROM sim_tick WHERE instrument_id = @i ORDER BY rowid DESC LIMIT @n
            """;
        cmd.Parameters.AddWithValue("@i", instrumentId);
        cmd.Parameters.AddWithValue("@n", Math.Max(1, limit));
        var list = new List<CtpTick>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            list.Add(new CtpTick
            {
                InstrumentId = r.GetString(0),
                TradingDay = r.GetString(1),
                ActionDay = r.GetString(2),
                UpdateTime = r.GetString(3),
                UpdateMillisec = r.GetInt32(4),
                LastPrice = r.GetDouble(5),
                Volume = r.GetInt64(6),
                BidPrice = r.GetDouble(7),
                BidVolume = r.GetInt32(8),
                AskPrice = r.GetDouble(9),
                AskVolume = r.GetInt32(10),
                OpenInterest = r.GetDouble(11),
                OpenPrice = r.GetDouble(12),
                Highest = r.GetDouble(13),
                Lowest = r.GetDouble(14),
                PreSettlement = r.GetDouble(15),
            });
        }
        list.Reverse();   // 查询 DESC 取最近 N 条 ⇒ 反转为时间升序（末位最新）
        return list;
    }
}
