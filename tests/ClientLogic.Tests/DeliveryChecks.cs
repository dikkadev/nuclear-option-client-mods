using NuclearDelivery;

internal static class DeliveryChecks
{
    public static void Run(Action<string, Action> test)
    {
        test("delivery radar boxes match camera size across native panel scales", () => {
            foreach (float radarScale in new[] { .69948f, .6646298f, 1f })
            {
                float matched = DeliveryLayout.CockpitScale(240 * 1.46f / radarScale, 240, 1, 1);
                if (Math.Abs(20 * matched * radarScale - 20 * 1.46f) > .001f)
                    throw new Exception("Radar font must retain the camera's rendered size.");
            }
        });
        test("delivery size matching respects camera and cockpit preferences with safe fallback", () => {
            float value = DeliveryLayout.CockpitScale(720, 360, 1.5f, .5f);
            if (Math.Abs(value - 1.5f) > .001f) throw new Exception("Size multipliers were lost.");
            foreach (float invalid in new[] { 0f, float.NaN, float.PositiveInfinity })
                if (DeliveryLayout.CockpitScale(invalid, 360, 1, 1) != 1)
                    throw new Exception("Unavailable geometry must retain a usable size.");
        });
        void Equal<T>(T expected, T actual)
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual))
                throw new Exception($"Expected {expected}, got {actual}");
        }
        void Counts(DeliveryLedger ledger, int mf, int mh, int mm, int bf, int bh, int bm)
        {
            var count = ledger.Snapshot();
            Equal(mf, count.MissileInFlight); Equal(mh, count.MissileHit); Equal(mm, count.MissileMissed);
            Equal(bf, count.BombInFlight); Equal(bh, count.BombHit); Equal(bm, count.BombMissed);
        }
        test("delivery categories and outcomes remain independent", () => {
            var ledger = new DeliveryLedger();
            for (int id = 1; id <= 6; id++) ledger.Launch(id, id <= 3 ? DeliveryKind.Missile : DeliveryKind.Bomb, 0);
            ledger.Resolve(2, true, 1); ledger.Resolve(3, false, 1);
            ledger.Resolve(5, true, 1); ledger.Resolve(6, false, 1);
            Counts(ledger, 1, 1, 1, 1, 1, 1);
        });
        test("delivery unknown outcome does not create a launch", () => {
            var ledger = new DeliveryLedger(); Equal(false, ledger.Resolve(9, true, 1));
            Counts(ledger, 0, 0, 0, 0, 0, 0);
        });
        test("delivery first outcome wins and duplicate does not refresh TTL", () => {
            var ledger = new DeliveryLedger(); ledger.Launch(1, DeliveryKind.Missile, 0);
            Equal(true, ledger.Resolve(1, true, 1)); Equal(false, ledger.Resolve(1, false, 3));
            Counts(ledger, 0, 1, 0, 0, 0, 0);
            ledger.Advance(4, 3, 120); Counts(ledger, 0, 0, 0, 0, 0, 0);
            Equal(false, ledger.Resolve(1, true, 5));
        });
        test("delivery recent TTL expires exactly at boundary without removing flight", () => {
            var ledger = new DeliveryLedger(); ledger.Launch(1, DeliveryKind.Bomb, 0);
            ledger.Launch(2, DeliveryKind.Missile, 0); ledger.Resolve(1, false, 2);
            ledger.Advance(4.999, 3, 120); Counts(ledger, 1, 0, 0, 0, 0, 1);
            ledger.Advance(5, 3, 120); Counts(ledger, 1, 0, 0, 0, 0, 0);
        });
        test("delivery flight failsafe grants full recent display duration", () => {
            var ledger = new DeliveryLedger(); ledger.Launch(1, DeliveryKind.Bomb, 10);
            ledger.Advance(129.999, 3, 120); Equal(true, ledger.IsInFlight(1));
            ledger.Advance(130, 3, 120); Counts(ledger, 0, 0, 0, 0, 0, 1);
            Equal(false, ledger.Resolve(1, true, 131));
            ledger.Advance(132.999, 3, 120); Equal(true, ledger.Contains(1));
            ledger.Advance(133, 3, 120); Equal(false, ledger.Contains(1));
        });
        test("delivery pooled instance relaunch resets kind outcome and time", () => {
            var ledger = new DeliveryLedger(); ledger.Launch(1, DeliveryKind.Missile, 0); ledger.Resolve(1, true, 1);
            ledger.Launch(1, DeliveryKind.Bomb, 100); Counts(ledger, 0, 0, 0, 1, 0, 0);
            ledger.Advance(120, 3, 120); Equal(true, ledger.IsInFlight(1));
            Equal(true, ledger.Resolve(1, false, 121)); Counts(ledger, 0, 0, 0, 0, 0, 1);
        });
        test("delivery simultaneous IDs do not deduplicate each other", () => {
            var ledger = new DeliveryLedger(); ledger.Launch(41, DeliveryKind.Missile, 0); ledger.Launch(42, DeliveryKind.Missile, 0);
            Equal(true, ledger.Resolve(41, false, 1)); Equal(true, ledger.Resolve(42, true, 1));
            Counts(ledger, 0, 1, 1, 0, 0, 0);
        });
        test("delivery forget and clear remove all transition state", () => {
            var ledger = new DeliveryLedger(); ledger.Launch(1, DeliveryKind.Missile, 0); ledger.Launch(2, DeliveryKind.Bomb, 0);
            ledger.Forget(1); Equal(false, ledger.Resolve(1, true, 1)); Counts(ledger, 0, 0, 0, 1, 0, 0);
            ledger.Clear(); Counts(ledger, 0, 0, 0, 0, 0, 0); Equal(false, ledger.Contains(2));
            ledger.Launch(2, DeliveryKind.Missile, 5); Equal(true, ledger.Resolve(2, true, 6));
        });
        test("delivery configurable durations replace defaults", () => {
            var ledger = new DeliveryLedger(); ledger.Launch(1, DeliveryKind.Bomb, 0);
            ledger.Advance(10, 8, 10); Counts(ledger, 0, 0, 0, 0, 0, 1);
            ledger.Advance(17.999, 8, 10); Equal(true, ledger.Contains(1));
            ledger.Advance(18, 8, 10); Equal(false, ledger.Contains(1));
        });
        test("delivery invalid durations use finite positive defaults", () => {
            foreach (double invalid in new[] { 0, -1, double.NaN, double.PositiveInfinity, double.NegativeInfinity })
            {
                var ledger = new DeliveryLedger(); ledger.Launch(1, DeliveryKind.Missile, 0);
                ledger.Advance(119.999, invalid, invalid); Equal(true, ledger.IsInFlight(1));
                ledger.Advance(120, invalid, invalid); Counts(ledger, 0, 0, 1, 0, 0, 0);
                ledger.Advance(123, invalid, invalid); Equal(false, ledger.Contains(1));
            }
        });
    }
}
