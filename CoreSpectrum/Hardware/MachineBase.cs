﻿using CoreSpectrum.Debug;
using CoreSpectrum.Enums;
using CoreSpectrum.Interfaces;
using CoreSpectrum.SupportClasses;
using Konamiman.Z80dotNet;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace CoreSpectrum.Hardware
{
    public abstract class MachineBase
    {
        protected readonly Breakpoint?[] _breakpoints = new Breakpoint?[65536];

        protected readonly Z80Processor _z80;
        protected readonly ISpectrumMemory _memory;
        protected readonly ULABase _ula;
        protected readonly TapePlayer _player;
        protected readonly MachineTimmings _timmings;
        protected readonly IAudioSampler _sampler;
        protected readonly IVideoRenderer _renderer;

        protected bool _pause;
        protected bool _stop = true;
        protected bool _turbo;
        protected bool _renderOnturbo;
        protected int _scanCycles;
        protected double _nextFrame;
        protected Stopwatch _sw = new Stopwatch();

        protected readonly double _ticksPerMilly;
        protected readonly double _ticksPerFrame;
        protected readonly double _millisPerFrame;
        protected readonly double _framesPerSecond;

        public event EventHandler<SpectrumFrameArgs>? FrameRendered;
        public event EventHandler<BreakPointEventArgs>? BreakpointHit;

        public bool Running { get { return !_stop; } }
        public bool Paused { get { return _pause; } }
        public bool TurboEnabled { get { return _turbo; } }
        public Z80Processor Z80 { get { return _z80; } }
        public ISpectrumMemory Memory { get { return _memory; } }
        public ULABase ULA { get { return _ula; } }
        public TapePlayer DataCorder { get { return _player; } }

        protected MachineBase(byte[][] RomSet, IVideoRenderer Renderer, IAudioSampler Sampler)
        {

            var hardware = GetHardware(RomSet, Renderer, Sampler);

            _timmings = hardware.Timmings;
            _memory = hardware.Memory;
            _ula = hardware.ULA;
            _renderer = Renderer;
            _sampler = Sampler;
            _player = new TapePlayer();
            _player.AudioChanged += TapePlayer_AudioChanged;

            _z80 = new Z80Processor();
            _z80.Memory = _memory;
            _z80.PortsSpace = _ula;
            _z80.RegisterInterruptSource(_ula);
            _z80.RegisterTStatesTarget(_player);
            _z80.RegisterTStatesTarget(_ula);
            _z80.SetMemoryAccessMode(0, 16384, MemoryAccessMode.ReadOnly);

            //Compute timmings
            _ticksPerMilly = Stopwatch.Frequency / 1000.0;
            _framesPerSecond = (double)_timmings.ProcessorSpeed / (double)(_timmings.TStatesPerScan * _timmings.ScansPerFrame);
            _millisPerFrame = (1.0 / _framesPerSecond) * 1000.0;
            _ticksPerFrame = _ticksPerMilly * _millisPerFrame;
            _z80.BeforeInstructionFetch += z80_BeforeInstructionFetch;
        }

        private void TapePlayer_AudioChanged(object? sender, AudioEventArgs e)
        {
            _ula.Ear = e.AudioLevel;
        }

        protected abstract MachineHardware GetHardware(byte[][] RomSet, IVideoRenderer Renderer, IAudioSampler Sampler);

        #region Machine execution
        protected virtual void SpectrumCycle(object? State)
        {
            _sw.Restart();

            long start = _sw.ElapsedTicks;
            _nextFrame = start + _ticksPerFrame;

            _scanCycles = 0;

            while (!_stop)
            {

                if (_pause)
                {
                    while (_pause && !_stop)
                        Thread.Sleep(1);

                    if (_stop)
                        return;

                    _nextFrame = _sw.ElapsedTicks;
                    _sampler.Resume(_z80.TStatesElapsedSinceStart);
                }

                NextInstruction();
            }

            _sw.Stop();

        }

        protected virtual void NextInstruction()
        {
            int instCycles = _z80.ExecuteNextInstruction();

            if (instCycles == 0)
            {
                Pause();
                return;
            }

            _scanCycles += instCycles;

            if (_scanCycles < _timmings.TStatesPerScan)
                return;

            _scanCycles -= _timmings.TStatesPerScan;

            _ula.ScanLine(_memory.GetVideoMemory());

            if (_ula.NewFrame && (!_turbo || _renderOnturbo))
            {
                if (FrameRendered != null)
                    FrameRendered(this, new SpectrumFrameArgs(_renderer));

                if (!_turbo)
                {
                    while (_nextFrame - _sw.ElapsedTicks > _ticksPerMilly * 2)
                        Thread.Sleep(1);

                    while (_sw.ElapsedTicks < _nextFrame - _ticksPerMilly) ;

                    _nextFrame += _ticksPerFrame;
                }
            }
        }

        #endregion

        #region Machine control

        public virtual void Start(bool backgroundThread = false)
        {
            _pause = false;
            _stop = false;
            _z80.Reset();
            _z80.Reset();

            if (!backgroundThread)
            {
                Thread th = new Thread(SpectrumCycle);
                th.Start();
            }
            else
                Task.Run(() => SpectrumCycle(null));

            _sampler.Play();
        }

        public virtual void Stop()
        {
            _stop = true;
            _sampler.Stop();
        }

        public virtual void Pause()
        {
            if (_stop)
                return;

            _pause = true;
            _sampler.Pause();
        }

        public virtual void Resume()
        {
            if (_stop)
                return;

            _pause = false;
            _sampler.Resume(_z80.TStatesElapsedSinceStart);
        }

        public virtual void Reset()
        {
            _z80.Reset();
        }

        public virtual void Turbo(bool Enable, bool RenderOnTurbo = false)
        {
            if (Enable)
            {
                _sampler.Pause();
                _turbo = true;
                _renderOnturbo = RenderOnTurbo;
            }
            else
            {
                bool paused = _pause;

                if (!paused && !_stop)
                {
                    Pause();
                    Thread.Sleep(10);
                }

                _turbo = false;

                if (!paused && !_stop)
                    Resume();
            }
        }

        public virtual void Step()
        {
            NextInstruction();
        }

        #endregion

        #region Keyboard handling

        public virtual void PressKey(SpectrumKeys Key)
        {
            _ula.PressKey(Key);
        }

        public virtual void ReleaseKey(SpectrumKeys Key)
        {
            _ula.ReleaseKey(Key);
        }

        #endregion

        #region Breakpoint handling
        protected virtual void z80_BeforeInstructionFetch(object? sender, Konamiman.Z80dotNet.BeforeInstructionFetchEventArgs e)
        {
            var bp = _breakpoints[_z80.Registers.PC];

            if (bp == null)
                return;

            if (bp.Executed)
            {
                bp.Executed = false;
                return;
            }

            if (bp.Temporary)
                _breakpoints[_z80.Registers.PC] = null;
            else
                bp.Executed = true;

            if (BreakpointHit != null)
            {
                var args = new BreakPointEventArgs(bp);
                BreakpointHit(this, args);

                if (args.StopExecution)
                    e.ExecutionStopper.Stop(true);
            }

        }

        public virtual void AddBreakpoint(Breakpoint BreakPoint)
        {
            _breakpoints[BreakPoint.Address] = BreakPoint;
        }

        public virtual void AddBreakpoints(IEnumerable<Breakpoint> BreakPoints)
        {
            foreach (var Breakpoint in BreakPoints)
                _breakpoints[Breakpoint.Address] = Breakpoint;
        }

        public virtual void RemoveBreakpoint(ushort Address)
        {
            _breakpoints[Address] = null;
        }

        public virtual void ClearBreakpoints()
        {
            Array.Fill(_breakpoints, null);
        }

        #endregion

        protected struct MachineTimmings
        {
            public required int TStatesPerScan { get; set; }
            public required int ScansPerFrame { get; set; }
            public required int ProcessorSpeed { get; set; }
        }

        protected class MachineHardware
        {
            public required MachineTimmings Timmings { get; set; }
            public required ULABase ULA { get; set; }
            public required ISpectrumMemory Memory { get; set; }
        
        }
    }

    #region Event args
    public class SpectrumFrameArgs : EventArgs
    {
        public SpectrumFrameArgs(IVideoRenderer renderer)
        {
            VideoRenderer = renderer;
        }
        public IVideoRenderer VideoRenderer { get; set; }
    }

    public class SpectrumAudioSampleArgs : EventArgs
    {
        public byte Sample { get; set; }
    }

    public class BreakPointEventArgs : EventArgs
    {
        public BreakPointEventArgs(Breakpoint breakpoint)
        {
            Breakpoint = breakpoint;
        }

        public Breakpoint Breakpoint { get; set; }
        public bool StopExecution { get; set; }
    }
    #endregion
}
