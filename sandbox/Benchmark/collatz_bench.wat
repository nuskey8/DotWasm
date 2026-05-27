(module
  (memory (export "memory") 1)

  (func (export "collatz_bench") (param $max_val i32)
    (local $i i32)
    (local $n i32)
    (local $steps i32)
    (local $mem_ptr i32)

    i32.const 1
    local.set $i
    
    i32.const 0
    local.set $mem_ptr

    (block $outer_break
      (loop $outer_loop
        ;; if (i > max_val) break;
        local.get $i
        local.get $max_val
        i32.gt_s
        br_if $outer_break

        ;; n = i, steps = 0
        local.get $i
        local.set $n
        i32.const 0
        local.set $steps

        (block $inner_break
          (loop $inner_loop
            ;; if (n == 1) break;
            local.get $n
            i32.const 1
            i32.eq
            br_if $inner_break

            ;; steps++
            local.get $steps
            i32.const 1
            i32.add
            local.set $steps

            ;; if (n % 2 == 0)
            local.get $n
            i32.const 1
            i32.and
            i32.const 0
            i32.eq
            if
              ;; n = n / 2
              local.get $n
              i32.const 1
              i32.shr_u
              local.set $n
            else
              ;; n = n * 3 + 1
              local.get $n
              i32.const 3
              i32.mul
              i32.const 1
              i32.add
              local.set $n
            end

            br $inner_loop
          )
        )

        local.get $mem_ptr
        local.get $steps
        i32.store

        local.get $mem_ptr
        i32.const 4
        i32.add
        local.set $mem_ptr

        ;; i++
        local.get $i
        i32.const 1
        i32.add
        local.set $i

        br $outer_loop
      )
    )
  )
)