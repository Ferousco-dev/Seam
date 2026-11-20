//! Locates the native X11 / GTK3 development libraries through `pkg-config`.
//!
//! We deliberately do not hard-code library directories or create symlinks to
//! distribution-specific shared objects: the build must work on any normal
//! Linux Mint / Ubuntu installation once the documented `-dev` packages are
//! installed.

use std::process::ExitCode;

/// `(pkg-config name, human readable package hint)`
const NATIVE_LIBS: &[(&str, &str)] = &[
    ("x11", "libx11-dev"),
    ("xfixes", "libxfixes-dev"),
    ("xi", "libxi-dev"),
    ("xrandr", "libxrandr-dev"),
    ("xtst", "libxtst-dev"),
    ("gtk+-3.0", "libgtk-3-dev"),
];

fn main() -> ExitCode {
    println!("cargo:rerun-if-changed=build.rs");
    println!("cargo:rerun-if-env-changed=SEAM_PKG_CONFIG");

    for (name, package) in NATIVE_LIBS {
        match pkg_config::Config::new().cargo_metadata(true).probe(name) {
            Ok(_) => {}
            Err(err) => {
                eprintln!(
                    "error: could not find native dependency `{name}` via pkg-config: {err}\n\
                     \n\
                     Install the SEAM Linux build dependencies with:\n\
                     \n\
                     \x20 sudo apt install build-essential pkg-config {package}\n"
                );
                return ExitCode::FAILURE;
            }
        }
    }

    ExitCode::SUCCESS
}
