: rls.btm -- construct a releast

setlocal
set rlsver=v10b2
set rlsdate=16f202
del andl-%rlsver%-%rlsdate%.zip 
zip andl-%rlsver%-%rlsdate%.zip readme.txt /r bin /r sample /r test /r thrift
