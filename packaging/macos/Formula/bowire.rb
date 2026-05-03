# Homebrew formula for Bowire. One file covers macOS arm64 + intel
# and Linux arm64 + intel — Homebrew picks the right archive at
# install time based on `OS.mac?` / `Hardware::CPU.arm?`.
#
# This file is the **template**. Placeholders (__VERSION__,
# __SHA256_*__) are substituted by .github/workflows/homebrew.yml on
# every release before the rendered file is pushed to
# kuestenlogik/homebrew-bowire.git.
#
# End-user install (after the tap is published):
#
#   brew tap kuestenlogik/bowire
#   brew install bowire
#
class Bowire < Formula
  desc "Interactive multi-protocol API workbench and CLI for .NET"
  homepage "https://github.com/Kuestenlogik/Bowire"
  version "__VERSION__"
  license "Apache-2.0"

  on_macos do
    on_arm do
      url "https://github.com/Kuestenlogik/Bowire/releases/download/v__VERSION__/bowire-osx-arm64.tar.gz"
      sha256 "__SHA256_MACOS_ARM64__"
    end
    on_intel do
      url "https://github.com/Kuestenlogik/Bowire/releases/download/v__VERSION__/bowire-osx-x64.tar.gz"
      sha256 "__SHA256_MACOS_X64__"
    end
  end

  on_linux do
    on_arm do
      url "https://github.com/Kuestenlogik/Bowire/releases/download/v__VERSION__/bowire-linux-arm64.tar.gz"
      sha256 "__SHA256_LINUX_ARM64__"
    end
    on_intel do
      url "https://github.com/Kuestenlogik/Bowire/releases/download/v__VERSION__/bowire-linux-x64.tar.gz"
      sha256 "__SHA256_LINUX_X64__"
    end
  end

  # libicu provides locale data so .NET's globalization stack doesn't
  # fall back to InvariantGlobalization. Linux-Brew picks it up the
  # same way macOS does — Homebrew handles the platform split.
  depends_on "icu4c"

  def install
    # The .NET self-contained publish lays its own framework + every
    # plugin DLL beside the entrypoint. They all need to live in one
    # directory because the runtime probes the assembly path relative
    # to bowire. libexec is Homebrew's idiomatic spot for that
    # (vs. cellar/lib which would require splitting); a thin symlink
    # in bin/ surfaces the executable on PATH.
    libexec.install Dir["*"]
    bin.install_symlink libexec/"bowire"
  end

  test do
    # Smoke test — pinned to --version so we don't trigger the full
    # discovery flow during `brew test`. A non-zero exit fails the
    # bottle build, so this catches packaging breakage early.
    assert_match version.to_s, shell_output("#{bin}/bowire --version")
  end
end
