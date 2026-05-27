(module
  (type $Point (struct (field $x (mut i32)) (field $y (mut i32))))

  (func $create_point (export "create_point") (param $x i32) (param $y i32) (result (ref $Point))
    local.get $x
    local.get $y
    struct.new $Point
  )

  (func $getX (export "get_x") (param $p (ref $Point)) (result i32)
    local.get $p
    struct.get $Point $x
  )

  (func $test (export "test") (result i32)
    (call $create_point (i32.const 10) (i32.const 20))
    call $getX
  )
)