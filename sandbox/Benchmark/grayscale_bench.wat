(module
  (memory (export "memory") 1)

  (func (export "grayscale_bench") (param $ptr i32) (param $pixels i32)
    (local $end i32)
    (local $r i32)
    (local $g i32)
    (local $b i32)
    (local $gray i32)

    local.get $ptr
    local.get $pixels
    i32.const 4
    i32.mul
    i32.add
    local.set $end

    (block $break
      (loop $loop
        local.get $ptr
        local.get $end
        i32.ge_u
        br_if $break

        local.get $ptr
        i32.load8_u
        local.set $r

        local.get $ptr
        i32.const 1
        i32.add
        i32.load8_u
        local.set $g

        local.get $ptr
        i32.const 2
        i32.add
        i32.load8_u
        local.set $b

        local.get $r
        i32.const 77
        i32.mul
        local.get $g
        i32.const 150
        i32.mul
        i32.add
        local.get $b
        i32.const 29
        i32.mul
        i32.add
        i32.const 8
        i32.shr_u
        local.set $gray

        local.get $ptr
        local.get $gray
        i32.store8

        local.get $ptr
        i32.const 1
        i32.add
        local.get $gray
        i32.store8

        local.get $ptr
        i32.const 2
        i32.add
        local.get $gray
        i32.store8

        local.get $ptr
        i32.const 4
        i32.add
        local.set $ptr

        br $loop
      )
    )
  )
)
