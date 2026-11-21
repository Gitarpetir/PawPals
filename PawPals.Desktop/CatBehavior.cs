using System;
using System.Collections.Generic;

namespace PawPals.Desktop
{
    internal enum CatState
    {
        Idle, Walking, Running, Sitting, Itching, Grooming1, Grooming2,
        Meowing, LayingDown, Sleeping1, Sleeping2, WakingUp, Stretching
    }

    internal sealed record AnimationSpec(string ActionName, int Frames, double FrameSeconds);

    // Aturan perilaku dipisahkan dari tampilan WPF agar mudah diubah dan diuji.
    internal sealed class CatBehavior
    {
        public static IReadOnlyDictionary<CatState, AnimationSpec> Animations { get; } =
            new Dictionary<CatState, AnimationSpec>
            {
                [CatState.Idle] = new("Idle", 10, 0.1),
                [CatState.Walking] = new("Walk", 8, 0.1),
                [CatState.Running] = new("Run", 8, 0.075),
                [CatState.Sitting] = new("Sitting", 1, 0.1),
                [CatState.Itching] = new("Itch", 2, 0.15),
                [CatState.Grooming1] = new("Licking 1", 5, 0.12),
                [CatState.Grooming2] = new("Licking 2", 5, 0.12),
                [CatState.Meowing] = new("Meow", 4, 0.12),
                [CatState.LayingDown] = new("Laying", 8, 0.12),
                [CatState.Sleeping1] = new("Sleeping1", 1, 0.1),
                [CatState.Sleeping2] = new("Sleeping2", 1, 0.1),
                // Bangun memakai urutan frame berbaring yang dibalik.
                [CatState.WakingUp] = new("Laying", 8, 0.1),
                [CatState.Stretching] = new("Stretching", 13, 0.1)
            };

        private readonly Random random;
        private int sequenceIndex;
        private int wakeStartFrame = 7;
        private double animationElapsed;
        private double stateElapsed;
        private double stateRemaining;
        private double awakeElapsed;
        private double nextNapAfter;
        private double clickCooldown;
        private bool wakingSequence;
        private bool canItch = true;
        private bool chatPaused;

        public CatState State { get; private set; } = CatState.Idle;
        public int Direction { get; private set; } = 1;
        public AnimationSpec Animation => Animations[State];
        public int FrameIndex => State == CatState.WakingUp
            ? wakeStartFrame - sequenceIndex : sequenceIndex;
        public bool IsSleeping => State is CatState.Sleeping1 or CatState.Sleeping2;
        public bool IsResting => IsSleeping || State == CatState.LayingDown;
        public bool CanRequestSleep => !IsResting && !wakingSequence;
        public double Speed => State switch
        {
            CatState.Walking => 80,
            CatState.Running => 160,
            _ => 0
        };
        public double BreathingScale => IsSleeping
            ? 1 + 0.025 * Math.Sin(stateElapsed * Math.PI * 2 / 3) : 1;

        private int ActiveFrameCount => State == CatState.WakingUp
            ? wakeStartFrame + 1 : Animation.Frames;
        private bool PlaysOnce => State is CatState.LayingDown or CatState.WakingUp
            or CatState.Stretching or CatState.Meowing;

        public CatBehavior(Random? random = null)
        {
            this.random = random ?? Random.Shared;
            nextNapAfter = this.random.Next(45, 91);
            BeginState(CatState.Idle);
        }

        public void Tick(double delta)
        {
            delta = Math.Clamp(delta, 0, 0.1);
            stateElapsed += delta;
            clickCooldown = Math.Max(0, clickCooldown - delta);
            if (!IsResting)
                awakeElapsed += delta;

            animationElapsed += delta;
            int steps = (int)((animationElapsed + 1e-9) / Animation.FrameSeconds);
            if (steps > 0)
            {
                animationElapsed = Math.Max(0,
                    animationElapsed - steps * Animation.FrameSeconds);
                sequenceIndex = PlaysOnce
                    ? Math.Min(ActiveFrameCount - 1, sequenceIndex + steps)
                    : (sequenceIndex + steps) % ActiveFrameCount;
            }

            if (chatPaused && State == CatState.Idle)
                return;

            stateRemaining -= delta;
            if (stateRemaining <= 1e-9)
                ChooseNextState();
        }

        public void SetCanItch(bool value)
        {
            canItch = value;
            if (!value && State == CatState.Itching)
                BeginState(CatState.Idle);
        }

        public void BeginChatPause()
        {
            chatPaused = true;
            if (!IsResting && !wakingSequence)
                BeginState(CatState.Idle);
        }

        public void EndChatPause()
        {
            chatPaused = false;
            if (pendingAction is CatState next)
            { pendingAction = null; wakingSequence = false; BeginState(next); }
        }

        public void ParkOffline()
        {
            wakingSequence = false; chatPaused = false; clickCooldown = 0;
            awakeElapsed = 0; BeginState(CatState.Sleeping1);
        }

        public void Face(int direction)
        {
            Direction = direction < 0 ? -1 : 1;
        }

        public bool RequestSleep()
        {
            if (!CanRequestSleep)
                return false;
            BeginState(CatState.LayingDown);
            return true;
        }

        // Perintah pemilik: aksi diantrekan saat chat masih menahan perpindahan.
        private CatState? pendingAction;
        public bool RequestAction(string action)
        {
            CatState? next = action switch
            {
                "run" => CatState.Running, "walk" => CatState.Walking,
                "sit" => CatState.Sitting, "groom" => CatState.Grooming1,
                "stretch" => CatState.Stretching, _ => null
            };
            if (next == null) return false;
            if (chatPaused) { pendingAction = next; return true; }
            wakingSequence = false; awakeElapsed = 0;
            BeginState(next.Value);
            return true;
        }

        public bool Click()
        {
            if (clickCooldown > 0 || wakingSequence)
                return false;
            clickCooldown = 0.45;

            if (IsResting)
                StartWakingUp();
            else
                BeginState(CatState.Meowing);
            return true;
        }

        private void StartWakingUp()
        {
            // Jika rebahan belum selesai, bangun dari frame saat ini.
            wakeStartFrame = State == CatState.LayingDown ? FrameIndex : 7;
            awakeElapsed = 0;
            nextNapAfter = random.Next(45, 91);
            wakingSequence = true;
            BeginState(CatState.WakingUp);
        }

        private void BeginState(CatState next)
        {
            State = next;
            sequenceIndex = 0;
            animationElapsed = 0;
            stateElapsed = 0;

            stateRemaining = State switch
            {
                CatState.Idle => random.Next(2, 5),
                CatState.Walking => random.Next(3, 7),
                CatState.Running => random.Next(2, 4),
                CatState.Sitting => random.Next(4, 9),
                CatState.Sleeping1 or CatState.Sleeping2 => random.Next(8, 16),
                CatState.Itching => Animation.Frames * Animation.FrameSeconds
                    * random.Next(3, 6),
                CatState.Grooming1 or CatState.Grooming2 =>
                    Animation.Frames * Animation.FrameSeconds * random.Next(2, 5),
                _ => ActiveFrameCount * Animation.FrameSeconds
            };

            if (State is CatState.Walking or CatState.Running)
                Direction = random.Next(2) == 0 ? -1 : 1;
        }

        private void ChooseNextState()
        {
            switch (State)
            {
                case CatState.LayingDown:
                    BeginState(CatState.Sleeping1);
                    return;
                case CatState.Sleeping1:
                    BeginState(CatState.Sleeping2);
                    return;
                case CatState.Sleeping2:
                    StartWakingUp();
                    return;
                case CatState.WakingUp:
                    BeginState(CatState.Stretching);
                    return;
                case CatState.Stretching when wakingSequence:
                    BeginState(CatState.Meowing);
                    return;
                case CatState.Meowing when wakingSequence:
                    wakingSequence = false;
                    BeginState(CatState.Idle);
                    return;
            }

            if (awakeElapsed >= nextNapAfter)
            {
                BeginState(CatState.LayingDown);
                return;
            }

            if (State != CatState.Idle)
            {
                BeginState(CatState.Idle);
                return;
            }

            // Jalan dan diam tetap dominan; aksi lainnya muncul sesekali.
            int choice = random.Next(100);
            BeginState(choice switch
            {
                < 35 => CatState.Walking,
                < 42 => CatState.Running,
                < 60 => CatState.Sitting,
                < 72 => CatState.Grooming1,
                < 80 => CatState.Grooming2,
                < 86 => canItch ? CatState.Itching : CatState.Grooming1,
                < 91 => CatState.Meowing,
                _ => CatState.Stretching
            });
        }
    }
}
