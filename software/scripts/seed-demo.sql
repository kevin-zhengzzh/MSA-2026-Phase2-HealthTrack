-- HealthTrack demo data — specs/06-ai-features-spec.md §11 (DM-1..DM-3)
--
-- Gives a demo account ~5 weeks of varied workouts and check-ins so the AI
-- chat assistant (and the weekly summary) have something to talk about.
--
-- Usage:
--   1. Register an account on the website whose username starts with "demo"
--      (e.g. demo_kevin). The prefix is a safety check — see below.
--   2. Set demo_username (and local_tz if you're not in New Zealand) below.
--   3. Paste this whole script into the Neon SQL Editor and run it.
--
-- Re-runnable: it REPLACES this user's workouts and check-ins from the last
-- 35 days (today is left alone, so you can still check in live during a demo).
-- Points, skins and other users are never touched.

DO $$
DECLARE
    demo_username text := 'demo';               -- <- your demo account's username
    local_tz      text := 'Pacific/Auckland';   -- <- the time zone you'll demo from
    uid           integer;
    today         date;
    last_monday   date;
    workouts      integer;
    checkins      integer;
BEGIN
    -- Refuse to rewrite a real account's history by mistake
    IF demo_username NOT ILIKE 'demo%' THEN
        RAISE EXCEPTION 'demo_username must start with "demo" (got "%")', demo_username;
    END IF;

    SELECT "Id" INTO uid FROM "Users" WHERE "Username" = demo_username;
    IF uid IS NULL THEN
        RAISE EXCEPTION 'No user named "%" — register it on the website first', demo_username;
    END IF;

    -- The app resolves "today" from the user's local date, not UTC
    today := (now() AT TIME ZONE local_tz)::date;
    -- Postgres weeks start on Monday, same as the app
    last_monday := date_trunc('week', today)::date - 7;

    DELETE FROM "WorkoutRecords" WHERE "UserId" = uid AND "Date" BETWEEN today - 35 AND today - 1;
    DELETE FROM "CheckIns"       WHERE "UserId" = uid AND "Date" BETWEEN today - 35 AND today - 1;

    -- Workouts, positioned relative to last week's Monday (offset 0).
    -- Last complete week totals 1,870 kcal against a 2,000 goal (94%), which
    -- gives the weekly summary a "so close" story. PointsEarned = 0 / Claimed
    -- = true so nothing shows up as an unclaimed daily reward.
    INSERT INTO "WorkoutRecords" ("UserId", "WorkoutType", "Calories", "Date", "CreatedAt", "PointsEarned", "Claimed")
    SELECT uid, w.workout_type, w.calories, x.day,
           (x.day + time '18:30') AT TIME ZONE local_tz, 0, true
    FROM (VALUES
        -- three weeks before last week
        (-21, 'Running', 380), (-19, 'Yoga', 160), (-17, 'Gym', 310), (-15, 'Running', 400),
        -- two weeks before
        (-14, 'Cycling', 450), (-11, 'Swimming', 500), (-9, 'Running', 350),
        -- the week before last
        (-7, 'Gym', 290), (-6, 'Running', 410), (-4, 'Yoga', 170), (-2, 'Cycling', 480),
        -- last complete week (Mon = 0 .. Sun = 6)
        (0, 'Running', 420), (1, 'Gym', 300), (3, 'Cycling', 520), (5, 'Yoga', 180), (6, 'Running', 450),
        -- this week — rows on or after today are dropped by the WHERE below
        (7, 'Swimming', 380), (8, 'Running', 360)
    ) AS w(offset_days, workout_type, calories)
    CROSS JOIN LATERAL (SELECT last_monday + w.offset_days AS day) AS x
    WHERE x.day < today AND x.day >= today - 35;
    GET DIAGNOSTICS workouts = ROW_COUNT;

    -- Check-ins: an unbroken run of 5 days ending yesterday (day 6 skipped on
    -- purpose), plus some older ones for the heatmap. Streak 5 makes "2 more
    -- check-ins until the reward skin" demoable; checking in live makes it 6.
    INSERT INTO "CheckIns" ("UserId", "Date", "Note", "CreatedAt", "PointsEarned", "Claimed")
    SELECT uid, today - n, NULL, ((today - n) + time '07:45') AT TIME ZONE local_tz, 10, true
    FROM unnest(ARRAY[1, 2, 3, 4, 5, 8, 9, 10, 11, 12, 16, 18, 20]) AS n;
    GET DIAGNOSTICS checkins = ROW_COUNT;

    -- Streak continuation is judged from the CheckIns rows above; Streak and
    -- LastCheckIn just mirror what the app would have stored after them.
    UPDATE "Users"
    SET "Streak" = 5,
        "LastCheckIn" = ((today - 1) + time '07:45') AT TIME ZONE local_tz,
        "WeeklyCalorieGoal" = 2000
    WHERE "Id" = uid;

    RAISE NOTICE 'Seeded user % (id %): % workouts, % check-ins; local today = %, last week = % .. %',
        demo_username, uid, workouts, checkins, today, last_monday, last_monday + 6;
END $$;
