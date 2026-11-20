#[derive(Debug, Clone, Copy)]
pub struct ScreenGeometry {
    pub width: u32,
    pub height: u32,
}

impl ScreenGeometry {
    pub fn new(width: u32, height: u32) -> Self {
        Self { width, height }
    }

    pub fn scale_to_fixed_u16(&self, x: i32, y: i32) -> (u16, u16) {
        let max_x = self.width.saturating_sub(1).max(1) as f64;
        let max_y = self.height.saturating_sub(1).max(1) as f64;
        let nx = ((x.clamp(0, self.width as i32) as f64 / max_x) * 65535.0).round() as u16;
        let ny = ((y.clamp(0, self.height as i32) as f64 / max_y) * 65535.0).round() as u16;
        (nx, ny)
    }

    pub fn project_from_fixed_u16(&self, nx: u16, ny: u16) -> (i32, i32) {
        let max_x = self.width.saturating_sub(1).max(1) as f64;
        let max_y = self.height.saturating_sub(1).max(1) as f64;
        let px = ((nx as f64 / 65535.0) * max_x).round() as i32;
        let py = ((ny as f64 / 65535.0) * max_y).round() as i32;
        (px, py)
    }
}
