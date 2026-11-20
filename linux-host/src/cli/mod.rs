pub mod commands;

pub fn execute_cli() {
    let args: Vec<String> = std::env::args().skip(1).collect();
    let cmd = args.first().map(|s| s.as_str()).unwrap_or("app");

    match cmd {
        "devices" => commands::run_list_devices(),
        "info" => commands::run_device_info(),
        "connect" => {
            let mut serial = None;
            let mut edge = None;
            let mut iter = args.iter().skip(1);
            while let Some(arg) = iter.next() {
                if arg == "--edge" {
                    edge = iter.next().map(|s| s.as_str());
                } else if !arg.starts_with('-') && serial.is_none() {
                    serial = Some(arg.as_str());
                }
            }
            commands::run_connect(serial, edge);
        }
        "disconnect" => {
            let serial = args.get(1).map(|s| s.as_str());
            commands::run_disconnect(serial);
        }
        "usb-test" => {
            let serial = args.get(1).map(|s| s.as_str());
            commands::run_usb_test(serial);
        }
        "wifi-test" => {
            let ip = args.get(1).map(|s| s.as_str());
            commands::run_wifi_test(ip);
        }
        "self-test" => commands::run_self_test_cli(),
        "diagnostics" => commands::run_diagnostics_cli(),
        "app" | "ui" => commands::run_app(),
        "-h" | "--help" | "help" => print_usage(),
        other => {
            eprintln!("Unknown command: '{}'", other);
            print_usage();
            std::process::exit(1);
        }
    }
}

fn print_usage() {
    println!("SEAM — Universal Control for Linux Mint (Cinnamon X11)");
    println!();
    println!("Usage:");
    println!("  seam app                 Launch native GTK3 graphical desktop application");
    println!("  seam devices             List attached Android devices discovered by ADB");
    println!("  seam info                Query connected Android display and input telemetry");
    println!("  seam connect [--edge <e>] Connect and start screen-edge input forwarding");
    println!("  seam disconnect [serial] Remove active ADB port forwards");
    println!("  seam usb-test [serial]   Run end-to-end USB transport and latency verification");
    println!("  seam wifi-test [ip]      Run end-to-end Wi-Fi TCP + UDP fast-path verification");
    println!("  seam self-test           Verify local X11, ADB, crypto, and clipboard readiness");
    println!("  seam diagnostics         Print comprehensive system diagnostics");
}
