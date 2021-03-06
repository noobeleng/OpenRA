#region Copyright & License Information
/*
 * Copyright 2007-2020 The OpenRA Developers (see AUTHORS)
 * This file is part of OpenRA, which is free software. It is made
 * available to you under the terms of the GNU General Public License
 * as published by the Free Software Foundation, either version 3 of
 * the License, or (at your option) any later version. For more
 * information, see COPYING.
 */
#endregion

using System;
using System.Runtime.InteropServices;
using OpenRA.Primitives;
using SDL2;

namespace OpenRA.Platforms.Default
{
	sealed class Sdl2PlatformWindow : ThreadAffine, IPlatformWindow
	{
		readonly IGraphicsContext context;
		readonly Sdl2Input input;

		public IGraphicsContext Context { get { return context; } }

		readonly IntPtr window;
		bool disposed;

		readonly object syncObject = new object();
		Size windowSize;
		Size surfaceSize;
		float windowScale = 1f;
		int2? lockedMousePosition;
		float scaleModifier;

		internal IntPtr Window
		{
			get
			{
				lock (syncObject)
					return window;
			}
		}

		public Size NativeWindowSize
		{
			get
			{
				lock (syncObject)
					return windowSize;
			}
		}

		public Size EffectiveWindowSize
		{
			get
			{
				lock (syncObject)
					return new Size((int)(windowSize.Width / scaleModifier), (int)(windowSize.Height / scaleModifier));
			}
		}

		public float NativeWindowScale
		{
			get
			{
				lock (syncObject)
					return windowScale;
			}
		}

		public float EffectiveWindowScale
		{
			get
			{
				lock (syncObject)
					return windowScale * scaleModifier;
			}
		}

		public Size SurfaceSize
		{
			get
			{
				lock (syncObject)
					return surfaceSize;
			}
		}

		public int CurrentDisplay
		{
			get
			{
				return SDL.SDL_GetWindowDisplayIndex(window);
			}
		}

		public int DisplayCount
		{
			get
			{
				return SDL.SDL_GetNumVideoDisplays();
			}
		}

		public event Action<float, float, float, float> OnWindowScaleChanged = (oldNative, oldEffective, newNative, newEffective) => { };

		[DllImport("user32.dll")]
		static extern bool SetProcessDPIAware();

		public Sdl2PlatformWindow(Size requestEffectiveWindowSize, WindowMode windowMode, float scaleModifier, int batchSize, int videoDisplay)
		{
			// Lock the Window/Surface properties until initialization is complete
			lock (syncObject)
			{
				this.scaleModifier = scaleModifier;

				// Disable legacy scaling on Windows
				if (Platform.CurrentPlatform == PlatformType.Windows)
					SetProcessDPIAware();

				SDL.SDL_Init(SDL.SDL_INIT_NOPARACHUTE | SDL.SDL_INIT_VIDEO);
				SDL.SDL_GL_SetAttribute(SDL.SDL_GLattr.SDL_GL_DOUBLEBUFFER, 1);
				SDL.SDL_GL_SetAttribute(SDL.SDL_GLattr.SDL_GL_RED_SIZE, 8);
				SDL.SDL_GL_SetAttribute(SDL.SDL_GLattr.SDL_GL_GREEN_SIZE, 8);
				SDL.SDL_GL_SetAttribute(SDL.SDL_GLattr.SDL_GL_BLUE_SIZE, 8);
				SDL.SDL_GL_SetAttribute(SDL.SDL_GLattr.SDL_GL_ALPHA_SIZE, 0);

				// Decide between OpenGL and OpenGL ES rendering
				// Test whether we can use the preferred renderer and fall back to the other if that fails
				// If neither works we will throw a graphics error later when trying to create the real window
				bool useGLES;
				if (Game.Settings.Graphics.PreferGLES)
					useGLES = CanCreateGLWindow(3, 0, SDL.SDL_GLprofile.SDL_GL_CONTEXT_PROFILE_ES);
				else
					useGLES = !CanCreateGLWindow(3, 2, SDL.SDL_GLprofile.SDL_GL_CONTEXT_PROFILE_CORE);

				var glMajor = 3;
				var glMinor = useGLES ? 0 : 2;
				var glProfile = useGLES ? SDL.SDL_GLprofile.SDL_GL_CONTEXT_PROFILE_ES : SDL.SDL_GLprofile.SDL_GL_CONTEXT_PROFILE_CORE;

				SDL.SDL_GL_SetAttribute(SDL.SDL_GLattr.SDL_GL_CONTEXT_MAJOR_VERSION, glMajor);
				SDL.SDL_GL_SetAttribute(SDL.SDL_GLattr.SDL_GL_CONTEXT_MINOR_VERSION, glMinor);
				SDL.SDL_GL_SetAttribute(SDL.SDL_GLattr.SDL_GL_CONTEXT_PROFILE_MASK, (int)glProfile);

				Console.WriteLine("Using SDL 2 with OpenGL{0} renderer", useGLES ? " ES" : "");

				if (videoDisplay < 0 || videoDisplay >= DisplayCount)
					videoDisplay = 0;

				SDL.SDL_DisplayMode display;
				SDL.SDL_GetCurrentDisplayMode(videoDisplay, out display);

				// Windows and Linux define window sizes in native pixel units.
				// Query the display/dpi scale so we can convert our requested effective size to pixels.
				// This is not necessary on macOS, which defines window sizes in effective units ("points").
				if (Platform.CurrentPlatform == PlatformType.Windows)
				{
					float ddpi, hdpi, vdpi;
					if (SDL.SDL_GetDisplayDPI(videoDisplay, out ddpi, out hdpi, out vdpi) == 0)
						windowScale = ddpi / 96;
				}
				else if (Platform.CurrentPlatform != PlatformType.OSX)
				{
					float scale = 1;
					var scaleVariable = Environment.GetEnvironmentVariable("OPENRA_DISPLAY_SCALE");
					if (scaleVariable != null && float.TryParse(scaleVariable, out scale))
						windowScale = scale;
				}

				Console.WriteLine("Desktop resolution: {0}x{1}", display.w, display.h);
				if (requestEffectiveWindowSize.Width == 0 && requestEffectiveWindowSize.Height == 0)
				{
					Console.WriteLine("No custom resolution provided, using desktop resolution");
					surfaceSize = windowSize = new Size(display.w, display.h);
				}
				else
					surfaceSize = windowSize = new Size((int)(requestEffectiveWindowSize.Width * windowScale), (int)(requestEffectiveWindowSize.Height * windowScale));

				Console.WriteLine("Using resolution: {0}x{1}", windowSize.Width, windowSize.Height);

				var windowFlags = SDL.SDL_WindowFlags.SDL_WINDOW_OPENGL | SDL.SDL_WindowFlags.SDL_WINDOW_ALLOW_HIGHDPI;

				// HiDPI doesn't work properly on OSX with (legacy) fullscreen mode
				if (Platform.CurrentPlatform == PlatformType.OSX && windowMode == WindowMode.Fullscreen)
					SDL.SDL_SetHint(SDL.SDL_HINT_VIDEO_HIGHDPI_DISABLED, "1");

				window = SDL.SDL_CreateWindow("OpenRA", SDL.SDL_WINDOWPOS_CENTERED_DISPLAY(videoDisplay), SDL.SDL_WINDOWPOS_CENTERED_DISPLAY(videoDisplay),
					windowSize.Width, windowSize.Height, windowFlags);

				// Work around an issue in macOS's GL backend where the window remains permanently black
				// (if dark mode is enabled) unless we drain the event queue before initializing GL
				if (Platform.CurrentPlatform == PlatformType.OSX)
				{
					SDL.SDL_Event e;
					while (SDL.SDL_PollEvent(out e) != 0)
					{
						// We can safely ignore all mouse/keyboard events and window size changes
						// (these will be caught in the window setup below), but do need to process focus
						if (e.type == SDL.SDL_EventType.SDL_WINDOWEVENT)
						{
							switch (e.window.windowEvent)
							{
								case SDL.SDL_WindowEventID.SDL_WINDOWEVENT_FOCUS_LOST:
									Game.HasInputFocus = false;
									break;

								case SDL.SDL_WindowEventID.SDL_WINDOWEVENT_FOCUS_GAINED:
									Game.HasInputFocus = true;
									break;
							}
						}
					}
				}

				// Enable high resolution rendering for Retina displays
				if (Platform.CurrentPlatform == PlatformType.OSX)
				{
					// OSX defines the window size in "points", with a device-dependent number of pixels per point.
					// The window scale is simply the ratio of GL pixels / window points.
					int width, height;

					SDL.SDL_GL_GetDrawableSize(Window, out width, out height);
					surfaceSize = new Size(width, height);
					windowScale = width * 1f / windowSize.Width;
				}
				else
					windowSize = new Size((int)(surfaceSize.Width / windowScale), (int)(surfaceSize.Height / windowScale));

				Console.WriteLine("Using window scale {0:F2}", windowScale);

				if (Game.Settings.Game.LockMouseWindow)
					GrabWindowMouseFocus();
				else
					ReleaseWindowMouseFocus();

				if (windowMode == WindowMode.Fullscreen)
				{
					SDL.SDL_SetWindowFullscreen(Window, (uint)SDL.SDL_WindowFlags.SDL_WINDOW_FULLSCREEN);

					// Fullscreen mode on OSX will ignore the configured display resolution
					// and instead always picks an arbitrary scaled resolution choice that may
					// not match the window size, leading to graphical and input issues.
					// We work around this by force disabling HiDPI and resetting the window and
					// surface sizes to match the size that is forced by SDL.
					// This is usually not what the player wants, but is the best we can consistently do.
					if (Platform.CurrentPlatform == PlatformType.OSX)
					{
						int width, height;
						SDL.SDL_GetWindowSize(Window, out width, out height);
						windowSize = surfaceSize = new Size(width, height);
						windowScale = 1;
					}
				}
				else if (windowMode == WindowMode.PseudoFullscreen)
				{
					SDL.SDL_SetWindowFullscreen(Window, (uint)SDL.SDL_WindowFlags.SDL_WINDOW_FULLSCREEN_DESKTOP);
					SDL.SDL_SetHint(SDL.SDL_HINT_VIDEO_MINIMIZE_ON_FOCUS_LOSS, "0");
				}
			}

			// Run graphics rendering on a dedicated thread.
			// The calling thread will then have more time to process other tasks, since rendering happens in parallel.
			// If the calling thread is the main game thread, this means it can run more logic and render ticks.
			// This is disabled on Windows because it breaks the ability to minimize/restore the window from the taskbar for reasons that we dont understand.
			var threadedRenderer = Platform.CurrentPlatform != PlatformType.Windows || !Game.Settings.Graphics.DisableWindowsRenderThread;
			if (!threadedRenderer)
			{
				var ctx = new Sdl2GraphicsContext(this);
				ctx.InitializeOpenGL();
				context = ctx;
			}
			else
				context = new ThreadedGraphicsContext(new Sdl2GraphicsContext(this), batchSize);

			context.SetVSyncEnabled(Game.Settings.Graphics.VSync);

			SDL.SDL_SetModState(SDL.SDL_Keymod.KMOD_NONE);
			input = new Sdl2Input();
		}

		byte[] DoublePixelData(byte[] data, Size size)
		{
			var scaledData = new byte[4 * data.Length];
			for (var y = 0; y < size.Height; y++)
			{
				for (var x = 0; x < size.Width; x++)
				{
					var a = 4 * (y * size.Width + x);
					var b = 8 * (2 * y * size.Width + x);
					var c = b + 8 * size.Width;
					for (var i = 0; i < 4; i++)
						scaledData[b + i] = scaledData[b + 4 + i] = scaledData[c + i] = scaledData[c + 4 + i] = data[a + i];
				}
			}

			return scaledData;
		}

		public IHardwareCursor CreateHardwareCursor(string name, Size size, byte[] data, int2 hotspot, bool pixelDouble)
		{
			VerifyThreadAffinity();
			try
			{
				// Pixel double the cursor on non-OSX if the window scale is large enough
				// OSX does this for us automatically
				if (Platform.CurrentPlatform != PlatformType.OSX && NativeWindowScale > 1.5f)
				{
					data = DoublePixelData(data, size);
					size = new Size(2 * size.Width, 2 * size.Height);
					hotspot *= 2;
				}

				// Scale all but the "default" cursor if requested by the player
				if (pixelDouble)
				{
					data = DoublePixelData(data, size);
					size = new Size(2 * size.Width, 2 * size.Height);
					hotspot *= 2;
				}

				return new Sdl2HardwareCursor(size, data, hotspot);
			}
			catch (Exception ex)
			{
				throw new Sdl2HardwareCursorException("Failed to create hardware cursor `{0}` - {1}".F(name, ex.Message), ex);
			}
		}

		public void SetHardwareCursor(IHardwareCursor cursor)
		{
			VerifyThreadAffinity();
			var c = cursor as Sdl2HardwareCursor;
			if (c == null)
				SDL.SDL_ShowCursor((int)SDL.SDL_bool.SDL_FALSE);
			else
			{
				SDL.SDL_ShowCursor((int)SDL.SDL_bool.SDL_TRUE);
				SDL.SDL_SetCursor(c.Cursor);
			}
		}

		public void SetRelativeMouseMode(bool mode)
		{
			if (mode)
			{
				int x, y;
				SDL.SDL_GetMouseState(out x, out y);
				lockedMousePosition = new int2(x, y);
			}
			else
			{
				if (lockedMousePosition.HasValue)
					SDL.SDL_WarpMouseInWindow(window, lockedMousePosition.Value.X, lockedMousePosition.Value.Y);

				lockedMousePosition = null;
			}
		}

		internal void WindowSizeChanged()
		{
			// The ratio between pixels and points can change when moving between displays in OSX
			// We need to recalculate our scale to account for the potential change in the actual rendered area
			if (Platform.CurrentPlatform == PlatformType.OSX)
			{
				int width, height;
				SDL.SDL_GL_GetDrawableSize(Window, out width, out height);

				if (width != SurfaceSize.Width || height != SurfaceSize.Height)
				{
					float oldScale;
					lock (syncObject)
					{
						oldScale = windowScale;
						surfaceSize = new Size(width, height);
						windowScale = width * 1f / windowSize.Width;
					}

					OnWindowScaleChanged(oldScale, oldScale * scaleModifier, windowScale, windowScale * scaleModifier);
				}
			}
		}

		public void Dispose()
		{
			if (disposed)
				return;

			disposed = true;

			if (context != null)
				context.Dispose();

			if (Window != IntPtr.Zero)
				SDL.SDL_DestroyWindow(Window);

			SDL.SDL_Quit();
		}

		public void GrabWindowMouseFocus()
		{
			VerifyThreadAffinity();
			SDL.SDL_SetWindowGrab(Window, SDL.SDL_bool.SDL_TRUE);
		}

		public void ReleaseWindowMouseFocus()
		{
			VerifyThreadAffinity();
			SDL.SDL_SetWindowGrab(Window, SDL.SDL_bool.SDL_FALSE);
		}

		public void PumpInput(IInputHandler inputHandler)
		{
			VerifyThreadAffinity();
			input.PumpInput(this, inputHandler, lockedMousePosition);

			if (lockedMousePosition.HasValue)
				SDL.SDL_WarpMouseInWindow(window, lockedMousePosition.Value.X, lockedMousePosition.Value.Y);
		}

		public string GetClipboardText()
		{
			VerifyThreadAffinity();
			return input.GetClipboardText();
		}

		public bool SetClipboardText(string text)
		{
			VerifyThreadAffinity();
			return input.SetClipboardText(text);
		}

		static bool CanCreateGLWindow(int major, int minor, SDL.SDL_GLprofile profile)
		{
			// Implementation inspired by TestIndividualGLVersion from Veldrid
			SDL.SDL_GL_SetAttribute(SDL.SDL_GLattr.SDL_GL_CONTEXT_MAJOR_VERSION, major);
			SDL.SDL_GL_SetAttribute(SDL.SDL_GLattr.SDL_GL_CONTEXT_MINOR_VERSION, minor);
			SDL.SDL_GL_SetAttribute(SDL.SDL_GLattr.SDL_GL_CONTEXT_PROFILE_MASK, (int)profile);

			var flags = SDL.SDL_WindowFlags.SDL_WINDOW_HIDDEN | SDL.SDL_WindowFlags.SDL_WINDOW_OPENGL;
			var window = SDL.SDL_CreateWindow("", 0, 0, 1, 1, flags);
			if (window == IntPtr.Zero || !string.IsNullOrEmpty(SDL.SDL_GetError()))
			{
				SDL.SDL_ClearError();
				return false;
			}

			var context = SDL.SDL_GL_CreateContext(window);
			if (context == IntPtr.Zero || SDL.SDL_GL_MakeCurrent(window, context) < 0)
			{
				SDL.SDL_ClearError();
				SDL.SDL_DestroyWindow(window);
				return false;
			}

			SDL.SDL_GL_DeleteContext(context);
			SDL.SDL_DestroyWindow(window);
			return true;
		}

		public void SetScaleModifier(float scale)
		{
			var oldScaleModifier = scaleModifier;
			scaleModifier = scale;
			OnWindowScaleChanged(windowScale, windowScale * oldScaleModifier, windowScale, windowScale * scaleModifier);
		}
	}
}
